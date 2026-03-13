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
    private async Task<int> RepairTimerDispatchesAsync(
        AzureOutboxService outbox,
        ISystemLease lease,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        lease.ThrowIfLost();
        await EnsureSchedulerFenceAsync(lease, cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        string upperBound = AzurePlatformRowKeys.TimerDispatch(ToUnixMilliseconds(now), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        int repaired = 0;

        await foreach (TableEntity dispatchEntity in resources.Table.Client.QueryAsync<TableEntity>(
                           filter: $"PartitionKey eq '{AzurePlatformTableConstants.PartitionKey}' and RowKey ge 'timer-dispatch|' and RowKey le '{upperBound}'",
                           maxPerPage: Math.Max(batchSize * 4, 20),
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (repaired >= batchSize)
            {
                break;
            }

            lease.ThrowIfLost();
            await EnsureSchedulerFenceAsync(lease, cancellationToken).ConfigureAwait(false);

            Guid timerId = ParseGuidSuffix(dispatchEntity.RowKey);
            NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.Timer(timerId),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.HasValue)
            {
                await DeleteEntityBestEffortAsync(dispatchEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            AzureSchedulerTimerModel timer = Deserialize<AzureSchedulerTimerModel>(response.Value!);
            if (timer.Status != StatusDispatching ||
                timer.DispatchVisibleAtUtc is null ||
                timer.DispatchVisibleAtUtc > now ||
                !string.Equals(timer.DispatchRowKey, dispatchEntity.RowKey, StringComparison.Ordinal))
            {
                await CleanupStaleRowAsync(timer.DispatchRowKey, dispatchEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (await TryDispatchTimerAsync(timer, outbox, cancellationToken).ConfigureAwait(false))
            {
                repaired++;
            }
        }

        return repaired;
    }

    private async Task<int> RepairJobRunDispatchesAsync(
        AzureOutboxService outbox,
        ISystemLease lease,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        lease.ThrowIfLost();
        await EnsureSchedulerFenceAsync(lease, cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        string upperBound = AzurePlatformRowKeys.JobRunDispatch(ToUnixMilliseconds(now), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        int repaired = 0;

        await foreach (TableEntity dispatchEntity in resources.Table.Client.QueryAsync<TableEntity>(
                           filter: $"PartitionKey eq '{AzurePlatformTableConstants.PartitionKey}' and RowKey ge 'job-run-dispatch|' and RowKey le '{upperBound}'",
                           maxPerPage: Math.Max(batchSize * 4, 20),
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (repaired >= batchSize)
            {
                break;
            }

            lease.ThrowIfLost();
            await EnsureSchedulerFenceAsync(lease, cancellationToken).ConfigureAwait(false);

            Guid runId = ParseGuidSuffix(dispatchEntity.RowKey);
            NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.JobRun(runId),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.HasValue)
            {
                await DeleteEntityBestEffortAsync(dispatchEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            AzureSchedulerJobRunModel run = Deserialize<AzureSchedulerJobRunModel>(response.Value!);
            if (run.Status != StatusDispatching ||
                run.DispatchVisibleAtUtc is null ||
                run.DispatchVisibleAtUtc > now ||
                !string.Equals(run.DispatchRowKey, dispatchEntity.RowKey, StringComparison.Ordinal))
            {
                await CleanupStaleRowAsync(run.DispatchRowKey, dispatchEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (await TryDispatchJobRunAsync(run, outbox, cancellationToken).ConfigureAwait(false))
            {
                repaired++;
            }
        }

        return repaired;
    }

    private async Task CompleteDispatchTimerAsync(Guid id, CancellationToken cancellationToken)
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
        if (timer.Status == StatusDone)
        {
            return;
        }

        if (timer.Status != StatusDispatching)
        {
            return;
        }

        string? dispatchRowKey = timer.DispatchRowKey;
        timer.Status = StatusDone;
        timer.LastError = null;
        timer.DispatchRowKey = null;
        timer.DispatchVisibleAtUtc = null;
        timer.CompletedUtc = timeProvider.GetUtcNow();

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, timer), response.Value!.ETag),
        ];

        TableEntity? dispatchEntity = await TryGetEntityAsync(dispatchRowKey, cancellationToken).ConfigureAwait(false);
        if (dispatchEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, dispatchEntity, dispatchEntity.ETag));
        }

        try
        {
            await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
            await resources.PayloadStore.DeleteIfPresentAsync(timer.Payload.PayloadBlobName, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception) || AzurePlatformExceptionHelper.IsNotFound(exception))
        {
            logger.LogDebug(exception, "Scheduler timer dispatch completion for {TimerId} lost an optimistic concurrency race.", id);
        }
    }

    private async Task CompleteDispatchJobRunAsync(Guid id, CancellationToken cancellationToken)
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
        if (run.Status == StatusDone)
        {
            return;
        }

        if (run.Status != StatusDispatching)
        {
            return;
        }

        string? dispatchRowKey = run.DispatchRowKey;
        run.Status = StatusDone;
        run.LastError = null;
        run.DispatchRowKey = null;
        run.DispatchVisibleAtUtc = null;
        run.CompletedUtc = timeProvider.GetUtcNow();

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, run), response.Value!.ETag),
        ];

        TableEntity? dispatchEntity = await TryGetEntityAsync(dispatchRowKey, cancellationToken).ConfigureAwait(false);
        if (dispatchEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, dispatchEntity, dispatchEntity.ETag));
        }

        try
        {
            await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
            await resources.PayloadStore.DeleteIfPresentAsync(run.Payload.PayloadBlobName, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception) || AzurePlatformExceptionHelper.IsNotFound(exception))
        {
            logger.LogDebug(exception, "Scheduler job run dispatch completion for {RunId} lost an optimistic concurrency race.", id);
        }
    }

    private async Task RequeueDispatchTimerAsync(Guid id, string lastError, CancellationToken cancellationToken)
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
        if (timer.Status != StatusDispatching)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        string? currentDispatchRowKey = timer.DispatchRowKey;
        DateTimeOffset visibleAtUtc = now.Add(GetDispatchBackoff(timer.DispatchAttemptCount));
        timer.LastError = lastError;
        timer.DispatchVisibleAtUtc = visibleAtUtc;
        timer.DispatchRowKey = AzurePlatformRowKeys.TimerDispatch(ToUnixMilliseconds(visibleAtUtc), timer.Id);

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, timer), response.Value!.ETag),
            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(timer.DispatchRowKey, "SchedulerTimerDispatch", new AzureSchedulerIndexModel { Id = timer.Id })),
        ];

        TableEntity? dispatchEntity = await TryGetEntityAsync(currentDispatchRowKey, cancellationToken).ConfigureAwait(false);
        if (dispatchEntity is not null)
        {
            actions.Insert(1, new TableTransactionAction(TableTransactionActionType.Delete, dispatchEntity, dispatchEntity.ETag));
        }

        try
        {
            await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
            await SignalIfReadyAsync(visibleAtUtc, now, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception) || AzurePlatformExceptionHelper.IsNotFound(exception))
        {
            logger.LogDebug(exception, "Scheduler timer dispatch retry for {TimerId} lost an optimistic concurrency race.", id);
        }
    }

    private async Task RequeueDispatchJobRunAsync(Guid id, string lastError, CancellationToken cancellationToken)
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
        if (run.Status != StatusDispatching)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        string? currentDispatchRowKey = run.DispatchRowKey;
        DateTimeOffset visibleAtUtc = now.Add(GetDispatchBackoff(run.DispatchAttemptCount));
        run.LastError = lastError;
        run.DispatchVisibleAtUtc = visibleAtUtc;
        run.DispatchRowKey = AzurePlatformRowKeys.JobRunDispatch(ToUnixMilliseconds(visibleAtUtc), run.Id);

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, run), response.Value!.ETag),
            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(run.DispatchRowKey, "SchedulerJobRunDispatch", new AzureSchedulerIndexModel { Id = run.Id })),
        ];

        TableEntity? dispatchEntity = await TryGetEntityAsync(currentDispatchRowKey, cancellationToken).ConfigureAwait(false);
        if (dispatchEntity is not null)
        {
            actions.Insert(1, new TableTransactionAction(TableTransactionActionType.Delete, dispatchEntity, dispatchEntity.ETag));
        }

        try
        {
            await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
            await SignalIfReadyAsync(visibleAtUtc, now, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception) || AzurePlatformExceptionHelper.IsNotFound(exception))
        {
            logger.LogDebug(exception, "Scheduler job run dispatch retry for {RunId} lost an optimistic concurrency race.", id);
        }
    }

    private async Task DeleteJobRunByIndexAsync(TableEntity indexEntity, CancellationToken cancellationToken)
    {
        AzureSchedulerIndexModel index = Deserialize<AzureSchedulerIndexModel>(indexEntity);
        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.JobRun(index.Id),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            await DeleteEntityBestEffortAsync(indexEntity, cancellationToken).ConfigureAwait(false);
            return;
        }

        AzureSchedulerJobRunModel run = Deserialize<AzureSchedulerJobRunModel>(response.Value!);
        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.Delete, response.Value!, response.Value!.ETag),
            new TableTransactionAction(TableTransactionActionType.Delete, indexEntity, indexEntity.ETag),
        ];

        TableEntity? dueEntity = await TryGetEntityAsync(run.DueRowKey, cancellationToken).ConfigureAwait(false);
        if (dueEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, dueEntity, dueEntity.ETag));
        }

        TableEntity? lockEntity = await TryGetEntityAsync(run.LockRowKey, cancellationToken).ConfigureAwait(false);
        if (lockEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag));
        }

        TableEntity? dispatchEntity = await TryGetEntityAsync(run.DispatchRowKey, cancellationToken).ConfigureAwait(false);
        if (dispatchEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, dispatchEntity, dispatchEntity.ETag));
        }

        try
        {
            await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
            await resources.PayloadStore.DeleteIfPresentAsync(run.Payload.PayloadBlobName, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception) || AzurePlatformExceptionHelper.IsNotFound(exception))
        {
            logger.LogDebug(exception, "Scheduler job run cleanup for {RunId} lost an optimistic concurrency race.", run.Id);
        }
    }

    private async Task CreateJobRunAsync(AzureSchedulerJobModel job, DateTimeOffset scheduledTimeUtc, CancellationToken cancellationToken)
    {
        string payload = await resources.PayloadStore.ReadTextAsync(job.Payload, cancellationToken).ConfigureAwait(false);
        Guid runId = Guid.NewGuid();
        AzurePayloadReference runPayload = await resources.PayloadStore
            .StoreTextAsync("scheduler/job-run", runId.ToString("N"), payload, cancellationToken)
            .ConfigureAwait(false);

        AzureSchedulerJobRunModel run = new()
        {
            Id = runId,
            JobId = job.JobId,
            JobName = job.JobName,
            Topic = job.Topic,
            Payload = runPayload,
            CreatedUtc = timeProvider.GetUtcNow(),
            ScheduledTimeUtc = scheduledTimeUtc,
            Status = StatusPending,
            DueRowKey = AzurePlatformRowKeys.JobRunDue(ToUnixMilliseconds(scheduledTimeUtc), runId),
            JobIndexRowKey = AzurePlatformRowKeys.JobRunByJob(job.JobId, runId),
        };

        try
        {
            await resources.Table.Client.SubmitTransactionAsync(
                    [
                        new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(AzurePlatformRowKeys.JobRun(runId), "SchedulerJobRun", run)),
                        new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(run.DueRowKey!, "SchedulerJobRunDue", new AzureSchedulerIndexModel { Id = run.Id })),
                        new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(run.JobIndexRowKey!, "SchedulerJobRunIndex", new AzureSchedulerIndexModel { Id = run.Id })),
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
            await SignalIfReadyAsync(scheduledTimeUtc, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await resources.PayloadStore.DeleteIfPresentAsync(runPayload.PayloadBlobName, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
