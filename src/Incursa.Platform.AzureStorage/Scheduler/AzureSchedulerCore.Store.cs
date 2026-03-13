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
    public async Task<DateTimeOffset?> GetNextEventTimeAsync(CancellationToken cancellationToken = default)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset? timerDue = await GetFirstDueTimeForPrefixAsync("timer-due|", cancellationToken).ConfigureAwait(false);
        DateTimeOffset? jobRunDue = await GetFirstDueTimeForPrefixAsync("job-run-due|", cancellationToken).ConfigureAwait(false);
        DateTimeOffset? jobDue = await GetFirstDueTimeForPrefixAsync("job-due|", cancellationToken).ConfigureAwait(false);
        DateTimeOffset? timerDispatch = await GetFirstDueTimeForPrefixAsync("timer-dispatch|", cancellationToken).ConfigureAwait(false);
        DateTimeOffset? jobRunDispatch = await GetFirstDueTimeForPrefixAsync("job-run-dispatch|", cancellationToken).ConfigureAwait(false);

        DateTimeOffset? next = null;
        foreach (DateTimeOffset candidate in new[] { timerDue, jobRunDue, jobDue, timerDispatch, jobRunDispatch }.Where(static value => value.HasValue).Select(static value => value!.Value))
        {
            next = !next.HasValue || candidate < next.Value ? candidate : next;
        }

        return next;
    }

    public async Task<int> CreateJobRunsFromDueJobsAsync(ISystemLease lease, CancellationToken cancellationToken = default)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        lease.ThrowIfLost();
        await EnsureSchedulerFenceAsync(lease, cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        string upperBound = AzurePlatformRowKeys.JobDue(ToUnixMilliseconds(now), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        int created = 0;

        await foreach (TableEntity dueEntity in resources.Table.Client.QueryAsync<TableEntity>(
                           filter: $"PartitionKey eq '{AzurePlatformTableConstants.PartitionKey}' and RowKey ge 'job-due|' and RowKey le '{upperBound}'",
                           maxPerPage: 50,
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (created >= 50)
            {
                break;
            }

            lease.ThrowIfLost();
            await EnsureSchedulerFenceAsync(lease, cancellationToken).ConfigureAwait(false);

            AzureSchedulerJobDueModel due = Deserialize<AzureSchedulerJobDueModel>(dueEntity);
            NullableResponse<TableEntity> jobResponse = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.Job(due.JobName),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!jobResponse.HasValue)
            {
                await DeleteEntityBestEffortAsync(dueEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            AzureSchedulerJobModel job = Deserialize<AzureSchedulerJobModel>(jobResponse.Value!);
            if (job.JobId != due.JobId ||
                job.NextDueTimeUtc > now ||
                !string.Equals(job.DueRowKey, dueEntity.RowKey, StringComparison.Ordinal))
            {
                await CleanupStaleRowAsync(job.DueRowKey, dueEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            DateTimeOffset nextDue = CalculateNextDueTime(job.CronSchedule, now);
            Guid runId = Guid.NewGuid();
            string payload = await resources.PayloadStore.ReadTextAsync(job.Payload, cancellationToken).ConfigureAwait(false);
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
                CreatedUtc = now,
                ScheduledTimeUtc = now,
                Status = StatusPending,
                DueRowKey = AzurePlatformRowKeys.JobRunDue(ToUnixMilliseconds(now), runId),
                JobIndexRowKey = AzurePlatformRowKeys.JobRunByJob(job.JobId, runId),
            };

            job.NextDueTimeUtc = nextDue;
            job.DueRowKey = AzurePlatformRowKeys.JobDue(ToUnixMilliseconds(nextDue), job.JobId);
            job.UpdatedUtc = now;

            try
            {
                await resources.Table.Client.SubmitTransactionAsync(
                        [
                            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(jobResponse.Value!, job), jobResponse.Value!.ETag),
                            new TableTransactionAction(TableTransactionActionType.Delete, dueEntity, dueEntity.ETag),
                            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(job.DueRowKey!, "SchedulerJobDue", new AzureSchedulerJobDueModel { JobId = job.JobId, JobName = job.JobName })),
                            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(AzurePlatformRowKeys.JobRun(runId), "SchedulerJobRun", run)),
                            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(run.DueRowKey!, "SchedulerJobRunDue", new AzureSchedulerIndexModel { Id = runId })),
                            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(run.JobIndexRowKey!, "SchedulerJobRunIndex", new AzureSchedulerIndexModel { Id = runId })),
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);

                created++;
                await resources.SignalQueue.SendSignalAsync(SignalName, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Scheduler due-job materialization for {JobName} lost an optimistic concurrency race.", job.JobName);
                await resources.PayloadStore.DeleteIfPresentAsync(runPayload.PayloadBlobName, cancellationToken).ConfigureAwait(false);
            }
        }

        return created;
    }

    public async Task<IReadOnlyList<(Guid Id, string Topic, string Payload)>> ClaimDueTimersAsync(ISystemLease lease, int batchSize, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AzureSchedulerTimerModel> claimed = await ClaimDispatchTimersInternalAsync(lease, batchSize, cancellationToken).ConfigureAwait(false);
        List<(Guid Id, string Topic, string Payload)> result = [];
        foreach (AzureSchedulerTimerModel timer in claimed)
        {
            string payload = await resources.PayloadStore.ReadTextAsync(timer.Payload, cancellationToken).ConfigureAwait(false);
            result.Add((timer.Id, timer.Topic, payload));
        }

        return result;
    }

    public async Task<IReadOnlyList<(Guid Id, Guid JobId, string Topic, string Payload)>> ClaimDueJobRunsAsync(ISystemLease lease, int batchSize, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AzureSchedulerJobRunModel> claimed = await ClaimDispatchJobRunsInternalAsync(lease, batchSize, cancellationToken).ConfigureAwait(false);
        List<(Guid Id, Guid JobId, string Topic, string Payload)> result = [];
        foreach (AzureSchedulerJobRunModel run in claimed)
        {
            string payload = await resources.PayloadStore.ReadTextAsync(run.Payload, cancellationToken).ConfigureAwait(false);
            result.Add((run.Id, run.JobId, run.Topic, payload));
        }

        return result;
    }

    public async Task UpdateSchedulerStateAsync(ISystemLease lease, CancellationToken cancellationToken = default)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        lease.ThrowIfLost();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.SchedulerState(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.HasValue)
            {
                AzureSchedulerStateModel state = new()
                {
                    CurrentFencingToken = lease.FencingToken,
                    LastRunAtUtc = timeProvider.GetUtcNow(),
                };

                try
                {
                    await resources.Table.Client.AddEntityAsync(CreateEntity(AzurePlatformRowKeys.SchedulerState(), "SchedulerState", state), cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
                {
                    logger.LogDebug(exception, "Scheduler state insert lost an optimistic concurrency race.");
                    continue;
                }
            }

            AzureSchedulerStateModel current = Deserialize<AzureSchedulerStateModel>(response.Value!);
            if (lease.FencingToken < current.CurrentFencingToken)
            {
                throw new LostLeaseException(LeaseResourceName, lease.OwnerToken);
            }

            current.CurrentFencingToken = lease.FencingToken;
            current.LastRunAtUtc = timeProvider.GetUtcNow();

            try
            {
                await resources.Table.Client.UpdateEntityAsync(
                        CreateEntity(response.Value!, current),
                        response.Value!.ETag,
                        TableUpdateMode.Replace,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Scheduler state update lost an optimistic concurrency race.");
            }
        }

        throw new InvalidOperationException("Scheduler state could not be updated after repeated optimistic concurrency retries.");
    }
}
