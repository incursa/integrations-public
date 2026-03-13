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
    public async Task<string> ScheduleTimerAsync(string topic, string payload, DateTimeOffset dueTime, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(payload);

        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset dueUtc = dueTime.ToUniversalTime();
        Guid timerId = Guid.NewGuid();
        AzurePayloadReference payloadReference = await resources.PayloadStore
            .StoreTextAsync("scheduler/timer", timerId.ToString("N"), payload, cancellationToken)
            .ConfigureAwait(false);

        AzureSchedulerTimerModel timer = new()
        {
            Id = timerId,
            Topic = topic,
            Payload = payloadReference,
            CreatedUtc = now,
            DueTimeUtc = dueUtc,
            Status = StatusPending,
            DueRowKey = AzurePlatformRowKeys.TimerDue(ToUnixMilliseconds(dueUtc), timerId),
        };

        await resources.Table.Client.SubmitTransactionAsync(
                [
                    new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(AzurePlatformRowKeys.Timer(timerId), "SchedulerTimer", timer)),
                    new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(timer.DueRowKey!, "SchedulerTimerDue", new AzureSchedulerIndexModel { Id = timerId })),
                ],
                cancellationToken)
            .ConfigureAwait(false);

        await SignalIfReadyAsync(dueUtc, now, cancellationToken).ConfigureAwait(false);
        return timerId.ToString();
    }

    public async Task<bool> CancelTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(timerId, out Guid parsedId))
        {
            return false;
        }

        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.Timer(parsedId),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.HasValue)
            {
                return false;
            }

            AzureSchedulerTimerModel timer = Deserialize<AzureSchedulerTimerModel>(response.Value!);
            if (timer.Status != StatusPending)
            {
                return false;
            }

            string? dueRowKey = timer.DueRowKey;
            timer.Status = StatusCancelled;
            timer.DueRowKey = null;
            timer.CompletedUtc = timeProvider.GetUtcNow();

            List<TableTransactionAction> actions =
            [
                new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, timer), response.Value!.ETag),
            ];

            TableEntity? dueEntity = await TryGetEntityAsync(dueRowKey, cancellationToken).ConfigureAwait(false);
            if (dueEntity is not null)
            {
                actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, dueEntity, dueEntity.ETag));
            }

            try
            {
                await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Scheduler timer cancellation for {TimerId} lost an optimistic concurrency race.", parsedId);
            }
        }

        return false;
    }

    public Task CreateOrUpdateJobAsync(string jobName, string topic, string cronSchedule, CancellationToken cancellationToken) =>
        CreateOrUpdateJobAsync(jobName, topic, cronSchedule, payload: null, cancellationToken);

    public async Task CreateOrUpdateJobAsync(string jobName, string topic, string cronSchedule, string? payload, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(cronSchedule);

        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset nextDue = CalculateNextDueTime(cronSchedule, now);
        AzurePayloadReference payloadReference = await resources.PayloadStore
            .StoreTextAsync("scheduler/job", AzurePlatformNameResolver.EncodeKey(jobName), payload ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        string jobRowKey = AzurePlatformRowKeys.Job(jobName);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    jobRowKey,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.HasValue)
            {
                Guid jobId = Guid.NewGuid();
                AzureSchedulerJobModel job = new()
                {
                    JobId = jobId,
                    JobName = jobName,
                    Topic = topic,
                    CronSchedule = cronSchedule,
                    Payload = payloadReference,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                    NextDueTimeUtc = nextDue,
                    DueRowKey = AzurePlatformRowKeys.JobDue(ToUnixMilliseconds(nextDue), jobId),
                };

                try
                {
                    await resources.Table.Client.SubmitTransactionAsync(
                            [
                                new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(jobRowKey, "SchedulerJob", job)),
                                new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(job.DueRowKey!, "SchedulerJobDue", new AzureSchedulerJobDueModel { JobId = jobId, JobName = jobName })),
                            ],
                            cancellationToken)
                        .ConfigureAwait(false);
                    await SignalIfReadyAsync(nextDue, now, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
                {
                    logger.LogDebug(exception, "Scheduler job create for {JobName} lost an optimistic concurrency race.", jobName);
                    continue;
                }
            }

            AzureSchedulerJobModel current = Deserialize<AzureSchedulerJobModel>(response.Value!);
            AzurePayloadReference previousPayload = current.Payload;
            string? previousDueRowKey = current.DueRowKey;

            current.Topic = topic;
            current.CronSchedule = cronSchedule;
            current.Payload = payloadReference;
            current.NextDueTimeUtc = nextDue;
            current.DueRowKey = AzurePlatformRowKeys.JobDue(ToUnixMilliseconds(nextDue), current.JobId);
            current.UpdatedUtc = now;

            List<TableTransactionAction> actions =
            [
                new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, current), response.Value!.ETag),
                new TableTransactionAction(TableTransactionActionType.UpsertReplace, CreateEntity(current.DueRowKey!, "SchedulerJobDue", new AzureSchedulerJobDueModel { JobId = current.JobId, JobName = current.JobName })),
            ];

            if (!string.Equals(previousDueRowKey, current.DueRowKey, StringComparison.Ordinal))
            {
                TableEntity? previousDue = await TryGetEntityAsync(previousDueRowKey, cancellationToken).ConfigureAwait(false);
                if (previousDue is not null)
                {
                    actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, previousDue, previousDue.ETag));
                }
            }

            try
            {
                await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
                await resources.PayloadStore.DeleteIfPresentAsync(previousPayload.PayloadBlobName, cancellationToken).ConfigureAwait(false);
                await SignalIfReadyAsync(nextDue, now, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Scheduler job update for {JobName} lost an optimistic concurrency race.", jobName);
            }
        }

        throw new InvalidOperationException($"Scheduler job '{jobName}' could not be created or updated after repeated optimistic concurrency retries.");
    }

    public async Task DeleteJobAsync(string jobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Job(jobName),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return;
        }

        AzureSchedulerJobModel job = Deserialize<AzureSchedulerJobModel>(response.Value!);
        string runPrefix = AzurePlatformRowKeys.JobRunByJob(job.JobId, Guid.Empty)[..^32];

        await foreach (TableEntity indexEntity in QueryPrefixAsync(runPrefix, cancellationToken).ConfigureAwait(false))
        {
            await DeleteJobRunByIndexAsync(indexEntity, cancellationToken).ConfigureAwait(false);
        }

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.Delete, response.Value!, response.Value!.ETag),
        ];

        TableEntity? dueEntity = await TryGetEntityAsync(job.DueRowKey, cancellationToken).ConfigureAwait(false);
        if (dueEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, dueEntity, dueEntity.ETag));
        }

        try
        {
            await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception) || AzurePlatformExceptionHelper.IsNotFound(exception))
        {
            logger.LogDebug(exception, "Scheduler job delete for {JobName} lost a race.", jobName);
        }

        await resources.PayloadStore.DeleteIfPresentAsync(job.Payload.PayloadBlobName, cancellationToken).ConfigureAwait(false);
    }

    public async Task TriggerJobAsync(string jobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Job(jobName),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return;
        }

        AzureSchedulerJobModel job = Deserialize<AzureSchedulerJobModel>(response.Value!);
        await CreateJobRunAsync(job, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }
}
