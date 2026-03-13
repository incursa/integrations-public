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
    internal async Task<int> RepairAndDispatchAsync(AzureOutboxService outbox, ISystemLease lease, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outbox);

        int completed = 0;
        completed += await RepairTimerDispatchesAsync(outbox, lease, batchSize, cancellationToken).ConfigureAwait(false);
        completed += await RepairJobRunDispatchesAsync(outbox, lease, batchSize, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<AzureSchedulerTimerModel> timers = await ClaimDispatchTimersInternalAsync(lease, batchSize, cancellationToken).ConfigureAwait(false);
        foreach (AzureSchedulerTimerModel timer in timers)
        {
            if (await TryDispatchTimerAsync(timer, outbox, cancellationToken).ConfigureAwait(false))
            {
                completed++;
            }
        }

        IReadOnlyList<AzureSchedulerJobRunModel> runs = await ClaimDispatchJobRunsInternalAsync(lease, batchSize, cancellationToken).ConfigureAwait(false);
        foreach (AzureSchedulerJobRunModel run in runs)
        {
            if (await TryDispatchJobRunAsync(run, outbox, cancellationToken).ConfigureAwait(false))
            {
                completed++;
            }
        }

        return completed;
    }

    private async Task<IReadOnlyList<AzureSchedulerTimerModel>> ClaimDispatchTimersInternalAsync(ISystemLease lease, int batchSize, CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        lease.ThrowIfLost();
        await EnsureSchedulerFenceAsync(lease, cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
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

            lease.ThrowIfLost();
            await EnsureSchedulerFenceAsync(lease, cancellationToken).ConfigureAwait(false);

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

            timer.Status = StatusDispatching;
            timer.DueRowKey = null;
            timer.DispatchAttemptCount = checked(timer.DispatchAttemptCount + 1);
            timer.DispatchVisibleAtUtc = now;
            timer.DispatchRowKey = AzurePlatformRowKeys.TimerDispatch(ToUnixMilliseconds(now), timer.Id);
            timer.OwnerToken = null;
            timer.LockedUntilUtc = null;
            timer.LockRowKey = null;
            timer.LastError = null;

            try
            {
                await resources.Table.Client.SubmitTransactionAsync(
                        [
                            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, timer), response.Value!.ETag),
                            new TableTransactionAction(TableTransactionActionType.Delete, dueEntity, dueEntity.ETag),
                            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(timer.DispatchRowKey!, "SchedulerTimerDispatch", new AzureSchedulerIndexModel { Id = timer.Id })),
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
                claimed.Add(timer);
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Scheduler timer dispatch-claim for {TimerId} lost an optimistic concurrency race.", timer.Id);
            }
        }

        return claimed;
    }

    private async Task<IReadOnlyList<AzureSchedulerJobRunModel>> ClaimDispatchJobRunsInternalAsync(ISystemLease lease, int batchSize, CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        lease.ThrowIfLost();
        await EnsureSchedulerFenceAsync(lease, cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
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

            lease.ThrowIfLost();
            await EnsureSchedulerFenceAsync(lease, cancellationToken).ConfigureAwait(false);

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

            run.Status = StatusDispatching;
            run.DueRowKey = null;
            run.DispatchAttemptCount = checked(run.DispatchAttemptCount + 1);
            run.DispatchVisibleAtUtc = now;
            run.DispatchRowKey = AzurePlatformRowKeys.JobRunDispatch(ToUnixMilliseconds(now), run.Id);
            run.OwnerToken = null;
            run.LockedUntilUtc = null;
            run.LockRowKey = null;
            run.LastError = null;

            try
            {
                await resources.Table.Client.SubmitTransactionAsync(
                        [
                            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, run), response.Value!.ETag),
                            new TableTransactionAction(TableTransactionActionType.Delete, dueEntity, dueEntity.ETag),
                            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(run.DispatchRowKey!, "SchedulerJobRunDispatch", new AzureSchedulerIndexModel { Id = run.Id })),
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
                claimed.Add(run);
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Scheduler job run dispatch-claim for {RunId} lost an optimistic concurrency race.", run.Id);
            }
        }

        return claimed;
    }

    private async Task<bool> TryDispatchTimerAsync(AzureSchedulerTimerModel timer, AzureOutboxService outbox, CancellationToken cancellationToken)
    {
        try
        {
            string payload = await resources.PayloadStore.ReadTextAsync(timer.Payload, cancellationToken).ConfigureAwait(false);
            await outbox.EnqueueDeterministicAsync($"scheduler:timer:{timer.Id:N}", timer.Topic, payload, timer.Id.ToString("N"), dueTimeUtc: null, cancellationToken).ConfigureAwait(false);
            await CompleteDispatchTimerAsync(timer.Id, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Scheduler timer {TimerId} could not be dispatched to the Azure outbox. It will be retried.", timer.Id);
            await RequeueDispatchTimerAsync(timer.Id, exception.ToString(), cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private async Task<bool> TryDispatchJobRunAsync(AzureSchedulerJobRunModel run, AzureOutboxService outbox, CancellationToken cancellationToken)
    {
        try
        {
            string payload = await resources.PayloadStore.ReadTextAsync(run.Payload, cancellationToken).ConfigureAwait(false);
            await outbox.EnqueueDeterministicAsync($"scheduler:job-run:{run.Id:N}", run.Topic, payload, run.Id.ToString("N"), dueTimeUtc: null, cancellationToken).ConfigureAwait(false);
            await CompleteDispatchJobRunAsync(run.Id, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Scheduler job run {JobRunId} could not be dispatched to the Azure outbox. It will be retried.", run.Id);
            await RequeueDispatchJobRunAsync(run.Id, exception.ToString(), cancellationToken).ConfigureAwait(false);
            return false;
        }
    }
}
