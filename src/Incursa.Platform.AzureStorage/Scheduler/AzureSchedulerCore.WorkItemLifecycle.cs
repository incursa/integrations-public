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
    private async Task CompleteClientTimerAsync(Guid id, OwnerToken ownerToken, CancellationToken cancellationToken)
    {
        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Timer(id),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return;
        }

        AzureSchedulerTimerModel timer = Deserialize<AzureSchedulerTimerModel>(response.Value!);
        if (timer.Status != StatusClaimed || !IsOwnedBy(timer.OwnerToken, ownerToken))
        {
            return;
        }

        string? lockRowKey = timer.LockRowKey;
        timer.Status = StatusDone;
        timer.OwnerToken = null;
        timer.LockedUntilUtc = null;
        timer.LockRowKey = null;
        timer.CompletedUtc = timeProvider.GetUtcNow();
        timer.LastError = null;

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, timer), response.Value!.ETag),
        ];

        TableEntity? lockEntity = await TryGetEntityAsync(lockRowKey, cancellationToken).ConfigureAwait(false);
        if (lockEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag));
        }

        await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteClientJobRunAsync(Guid id, OwnerToken ownerToken, CancellationToken cancellationToken)
    {
        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.JobRun(id),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return;
        }

        AzureSchedulerJobRunModel run = Deserialize<AzureSchedulerJobRunModel>(response.Value!);
        if (run.Status != StatusClaimed || !IsOwnedBy(run.OwnerToken, ownerToken))
        {
            return;
        }

        string? lockRowKey = run.LockRowKey;
        run.Status = StatusDone;
        run.OwnerToken = null;
        run.LockedUntilUtc = null;
        run.LockRowKey = null;
        run.CompletedUtc = timeProvider.GetUtcNow();
        run.LastError = null;

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, run), response.Value!.ETag),
        ];

        TableEntity? lockEntity = await TryGetEntityAsync(lockRowKey, cancellationToken).ConfigureAwait(false);
        if (lockEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag));
        }

        await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
    }

    private async Task RescheduleClientTimerAsync(Guid id, OwnerToken ownerToken, CancellationToken cancellationToken)
    {
        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Timer(id),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        AzureSchedulerTimerModel timer = Deserialize<AzureSchedulerTimerModel>(response.Value!);
        if (timer.Status != StatusClaimed || !IsOwnedBy(timer.OwnerToken, ownerToken))
        {
            return;
        }

        string? lockRowKey = timer.LockRowKey;
        timer.Status = StatusPending;
        timer.OwnerToken = null;
        timer.LockedUntilUtc = null;
        timer.LockRowKey = null;
        timer.RetryCount = checked(timer.RetryCount + 1);
        timer.DueTimeUtc = now;
        timer.DueRowKey = AzurePlatformRowKeys.TimerDue(ToUnixMilliseconds(now), id);

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, timer), response.Value!.ETag),
            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(timer.DueRowKey!, "SchedulerTimerDue", new AzureSchedulerIndexModel { Id = timer.Id })),
        ];

        TableEntity? lockEntity = await TryGetEntityAsync(lockRowKey, cancellationToken).ConfigureAwait(false);
        if (lockEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag));
        }

        await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
        await SignalIfReadyAsync(now, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task RescheduleClientJobRunAsync(Guid id, OwnerToken ownerToken, CancellationToken cancellationToken)
    {
        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.JobRun(id),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        AzureSchedulerJobRunModel run = Deserialize<AzureSchedulerJobRunModel>(response.Value!);
        if (run.Status != StatusClaimed || !IsOwnedBy(run.OwnerToken, ownerToken))
        {
            return;
        }

        string? lockRowKey = run.LockRowKey;
        run.Status = StatusPending;
        run.OwnerToken = null;
        run.LockedUntilUtc = null;
        run.LockRowKey = null;
        run.RetryCount = checked(run.RetryCount + 1);
        run.ScheduledTimeUtc = now;
        run.DueRowKey = AzurePlatformRowKeys.JobRunDue(ToUnixMilliseconds(now), id);

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, run), response.Value!.ETag),
            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(run.DueRowKey!, "SchedulerJobRunDue", new AzureSchedulerIndexModel { Id = run.Id })),
        ];

        TableEntity? lockEntity = await TryGetEntityAsync(lockRowKey, cancellationToken).ConfigureAwait(false);
        if (lockEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag));
        }

        await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
        await SignalIfReadyAsync(now, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReapExpiredClientTimersAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        string upperBound = AzurePlatformRowKeys.TimerLock(ToUnixMilliseconds(now), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));

        await foreach (TableEntity lockEntity in resources.Table.Client.QueryAsync<TableEntity>(
                           filter: $"PartitionKey eq '{AzurePlatformTableConstants.PartitionKey}' and RowKey ge 'timer-lock|' and RowKey le '{upperBound}'",
                           maxPerPage: 100,
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            Guid timerId = ParseGuidSuffix(lockEntity.RowKey);
            NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.Timer(timerId),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.HasValue)
            {
                await DeleteEntityBestEffortAsync(lockEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            AzureSchedulerTimerModel timer = Deserialize<AzureSchedulerTimerModel>(response.Value!);
            if (timer.Status != StatusClaimed ||
                timer.LockedUntilUtc is null ||
                timer.LockedUntilUtc > now ||
                !string.Equals(timer.LockRowKey, lockEntity.RowKey, StringComparison.Ordinal))
            {
                continue;
            }

            timer.Status = StatusPending;
            timer.OwnerToken = null;
            timer.LockedUntilUtc = null;
            timer.LockRowKey = null;
            timer.RetryCount = checked(timer.RetryCount + 1);
            timer.DueTimeUtc = now;
            timer.DueRowKey = AzurePlatformRowKeys.TimerDue(ToUnixMilliseconds(now), timer.Id);

            try
            {
                await resources.Table.Client.SubmitTransactionAsync(
                        [
                            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, timer), response.Value!.ETag),
                            new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag),
                            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(timer.DueRowKey!, "SchedulerTimerDue", new AzureSchedulerIndexModel { Id = timer.Id })),
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
                await resources.SignalQueue.SendSignalAsync(SignalName, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Scheduler timer reap for {TimerId} lost an optimistic concurrency race.", timer.Id);
            }
        }
    }

    private async Task ReapExpiredClientJobRunsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        string upperBound = AzurePlatformRowKeys.JobRunLock(ToUnixMilliseconds(now), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));

        await foreach (TableEntity lockEntity in resources.Table.Client.QueryAsync<TableEntity>(
                           filter: $"PartitionKey eq '{AzurePlatformTableConstants.PartitionKey}' and RowKey ge 'job-run-lock|' and RowKey le '{upperBound}'",
                           maxPerPage: 100,
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            Guid runId = ParseGuidSuffix(lockEntity.RowKey);
            NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.JobRun(runId),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.HasValue)
            {
                await DeleteEntityBestEffortAsync(lockEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            AzureSchedulerJobRunModel run = Deserialize<AzureSchedulerJobRunModel>(response.Value!);
            if (run.Status != StatusClaimed ||
                run.LockedUntilUtc is null ||
                run.LockedUntilUtc > now ||
                !string.Equals(run.LockRowKey, lockEntity.RowKey, StringComparison.Ordinal))
            {
                continue;
            }

            run.Status = StatusPending;
            run.OwnerToken = null;
            run.LockedUntilUtc = null;
            run.LockRowKey = null;
            run.RetryCount = checked(run.RetryCount + 1);
            run.ScheduledTimeUtc = now;
            run.DueRowKey = AzurePlatformRowKeys.JobRunDue(ToUnixMilliseconds(now), run.Id);

            try
            {
                await resources.Table.Client.SubmitTransactionAsync(
                        [
                            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, run), response.Value!.ETag),
                            new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag),
                            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(run.DueRowKey!, "SchedulerJobRunDue", new AzureSchedulerIndexModel { Id = run.Id })),
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
                await resources.SignalQueue.SendSignalAsync(SignalName, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Scheduler job run reap for {RunId} lost an optimistic concurrency race.", run.Id);
            }
        }
    }
}
