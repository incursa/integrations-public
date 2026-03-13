// Copyright (c) Incursa
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Incursa.Platform;

internal sealed partial class AzureSchedulerCore
{
    public async Task<IReadOnlyList<Guid>> ClaimTimersAsync(OwnerToken ownerToken, int leaseSeconds, int batchSize, CancellationToken cancellationToken)
    {
        IReadOnlyList<AzureSchedulerTimerModel> claimed = await ClaimClientTimersInternalAsync(ownerToken, leaseSeconds, batchSize, cancellationToken).ConfigureAwait(false);
        return claimed.Select(static timer => timer.Id).ToList();
    }

    public async Task<IReadOnlyList<Guid>> ClaimJobRunsAsync(OwnerToken ownerToken, int leaseSeconds, int batchSize, CancellationToken cancellationToken)
    {
        IReadOnlyList<AzureSchedulerJobRunModel> claimed = await ClaimClientJobRunsInternalAsync(ownerToken, leaseSeconds, batchSize, cancellationToken).ConfigureAwait(false);
        return claimed.Select(static run => run.Id).ToList();
    }

    public async Task AckTimersAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        foreach (Guid id in ids)
        {
            await CompleteClientTimerAsync(id, ownerToken, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AckJobRunsAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        foreach (Guid id in ids)
        {
            await CompleteClientJobRunAsync(id, ownerToken, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AbandonTimersAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        foreach (Guid id in ids)
        {
            await RescheduleClientTimerAsync(id, ownerToken, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AbandonJobRunsAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        foreach (Guid id in ids)
        {
            await RescheduleClientJobRunAsync(id, ownerToken, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ReapExpiredTimersAsync(CancellationToken cancellationToken) =>
        ReapExpiredClientTimersAsync(cancellationToken);

    public Task ReapExpiredJobRunsAsync(CancellationToken cancellationToken) =>
        ReapExpiredClientJobRunsAsync(cancellationToken);

    private async Task<IReadOnlyList<AzureSchedulerTimerModel>> ClaimClientTimersInternalAsync(OwnerToken ownerToken, int leaseSeconds, int batchSize, CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset lockedUntil = now.AddSeconds(leaseSeconds);
        string upperBound = AzurePlatformRowKeys.TimerDue(ToUnixMilliseconds(now), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        List<AzureSchedulerTimerModel> claimed = [];

        await foreach (TableEntity dueEntity in resources.Table.Client.QueryAsync<TableEntity>(
                           filter: $"PartitionKey eq '{AzurePlatformTableConstants.PartitionKey}' and RowKey ge 'timer-due|' and RowKey le '{upperBound}'",
                           maxPerPage: Math.Max(batchSize * 4, 20),
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (claimed.Count >= batchSize)
            {
                break;
            }

            Guid timerId = ParseGuidSuffix(dueEntity.RowKey);
            NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.Timer(timerId),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.HasValue)
            {
                await DeleteEntityBestEffortAsync(dueEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            AzureSchedulerTimerModel timer = Deserialize<AzureSchedulerTimerModel>(response.Value!);
            if (timer.Status != StatusPending ||
                timer.DueTimeUtc > now ||
                !string.Equals(timer.DueRowKey, dueEntity.RowKey, StringComparison.Ordinal))
            {
                await CleanupStaleRowAsync(timer.DueRowKey, dueEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            timer.Status = StatusClaimed;
            timer.OwnerToken = ownerToken.Value.ToString("N");
            timer.LockedUntilUtc = lockedUntil;
            timer.LockRowKey = AzurePlatformRowKeys.TimerLock(ToUnixMilliseconds(lockedUntil), timerId);
            timer.DueRowKey = null;

            try
            {
                await resources.Table.Client.SubmitTransactionAsync(
                        [
                            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, timer), response.Value!.ETag),
                            new TableTransactionAction(TableTransactionActionType.Delete, dueEntity, dueEntity.ETag),
                            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(timer.LockRowKey!, "SchedulerTimerLock", new AzureSchedulerIndexModel { Id = timer.Id })),
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
                claimed.Add(timer);
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Scheduler timer claim for {TimerId} lost an optimistic concurrency race.", timer.Id);
            }
        }

        return claimed;
    }

    private async Task<IReadOnlyList<AzureSchedulerJobRunModel>> ClaimClientJobRunsInternalAsync(OwnerToken ownerToken, int leaseSeconds, int batchSize, CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset lockedUntil = now.AddSeconds(leaseSeconds);
        string upperBound = AzurePlatformRowKeys.JobRunDue(ToUnixMilliseconds(now), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        List<AzureSchedulerJobRunModel> claimed = [];

        await foreach (TableEntity dueEntity in resources.Table.Client.QueryAsync<TableEntity>(
                           filter: $"PartitionKey eq '{AzurePlatformTableConstants.PartitionKey}' and RowKey ge 'job-run-due|' and RowKey le '{upperBound}'",
                           maxPerPage: Math.Max(batchSize * 4, 20),
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (claimed.Count >= batchSize)
            {
                break;
            }

            Guid runId = ParseGuidSuffix(dueEntity.RowKey);
            NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.JobRun(runId),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.HasValue)
            {
                await DeleteEntityBestEffortAsync(dueEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            AzureSchedulerJobRunModel run = Deserialize<AzureSchedulerJobRunModel>(response.Value!);
            if (run.Status != StatusPending ||
                run.ScheduledTimeUtc > now ||
                !string.Equals(run.DueRowKey, dueEntity.RowKey, StringComparison.Ordinal))
            {
                await CleanupStaleRowAsync(run.DueRowKey, dueEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            run.Status = StatusClaimed;
            run.OwnerToken = ownerToken.Value.ToString("N");
            run.LockedUntilUtc = lockedUntil;
            run.LockRowKey = AzurePlatformRowKeys.JobRunLock(ToUnixMilliseconds(lockedUntil), runId);
            run.DueRowKey = null;

            try
            {
                await resources.Table.Client.SubmitTransactionAsync(
                        [
                            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, run), response.Value!.ETag),
                            new TableTransactionAction(TableTransactionActionType.Delete, dueEntity, dueEntity.ETag),
                            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(run.LockRowKey!, "SchedulerJobRunLock", new AzureSchedulerIndexModel { Id = run.Id })),
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
                claimed.Add(run);
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Scheduler job run claim for {RunId} lost an optimistic concurrency race.", run.Id);
            }
        }

        return claimed;
    }
}
