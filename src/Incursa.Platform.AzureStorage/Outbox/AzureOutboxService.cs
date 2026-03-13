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

using System.Data;
using Azure;
using Azure.Data.Tables;
using Incursa.Platform.Outbox;
using Microsoft.Extensions.Logging;

namespace Incursa.Platform;

internal sealed class AzureOutboxService : IOutbox
{
    private const byte StatusReady = 0;
    private const byte StatusInProgress = 1;
    private const byte StatusDone = 2;
    private const byte StatusFailed = 3;
    private const string SignalName = "outbox-ready";

    private readonly AzureOutboxResources resources;
    private readonly TimeProvider timeProvider;
    private readonly IOutboxJoinStore joinStore;
    private readonly ILogger<AzureOutboxService> logger;

    public AzureOutboxService(
        AzureOutboxResources resources,
        TimeProvider timeProvider,
        IOutboxJoinStore joinStore,
        ILogger<AzureOutboxService> logger)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.joinStore = joinStore ?? throw new ArgumentNullException(nameof(joinStore));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal AzureOutboxResources Resources => resources;

    public Task EnqueueAsync(string topic, string payload, CancellationToken cancellationToken) =>
        EnqueueAsync(topic, payload, correlationId: null, dueTimeUtc: null, cancellationToken);

    public Task EnqueueAsync(string topic, string payload, string? correlationId, CancellationToken cancellationToken) =>
        EnqueueAsync(topic, payload, correlationId, dueTimeUtc: null, cancellationToken);

    public async Task EnqueueAsync(
        string topic,
        string payload,
        string? correlationId,
        DateTimeOffset? dueTimeUtc,
        CancellationToken cancellationToken)
    {
        await EnqueueInternalAsync(
                topic,
                payload,
                correlationId,
                dueTimeUtc,
                Guid.NewGuid(),
                Guid.NewGuid(),
                idempotencyKey: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal Task EnqueueDeterministicAsync(
        string idempotencyKey,
        string topic,
        string payload,
        string? correlationId,
        DateTimeOffset? dueTimeUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        Guid workItemId = AzurePlatformDeterministicGuid.Create("azure-outbox-work-item", idempotencyKey);
        Guid messageId = AzurePlatformDeterministicGuid.Create("azure-outbox-message", idempotencyKey);

        return EnqueueInternalAsync(
            topic,
            payload,
            correlationId,
            dueTimeUtc,
            workItemId,
            messageId,
            idempotencyKey,
            cancellationToken);
    }

    private async Task EnqueueInternalAsync(
        string topic,
        string payload,
        string? correlationId,
        DateTimeOffset? dueTimeUtc,
        Guid workItemId,
        Guid messageId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(payload);

        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset effectiveDue = (dueTimeUtc ?? now).ToUniversalTime();
        AzurePayloadReference payloadReference = await resources.PayloadStore
            .StoreTextAsync("outbox", workItemId.ToString("N"), payload, cancellationToken)
            .ConfigureAwait(false);

        AzureOutboxRecordModel model = new()
        {
            Id = workItemId,
            MessageId = messageId,
            Topic = topic,
            CorrelationId = correlationId,
            CreatedAt = now,
            DueTimeUtc = effectiveDue,
            Status = StatusReady,
            RetryCount = 0,
            Payload = payloadReference,
            DueRowKey = AzurePlatformRowKeys.Due(ToUnixMilliseconds(effectiveDue), workItemId.ToString("N")),
        };

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(AzurePlatformRowKeys.Item(workItemId.ToString("N")), "OutboxItem", model)),
            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(model.DueRowKey!, "OutboxDue", new AzureOutboxIndexModel { Id = workItemId })),
        ];

        try
        {
            await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
            await SignalIfReadyAsync(effectiveDue, now, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception) && idempotencyKey is not null)
        {
            bool alreadyPresent = await MatchesExistingDeterministicMessageAsync(
                    workItemId,
                    topic,
                    correlationId,
                    payloadReference,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!alreadyPresent)
            {
                throw;
            }

            await resources.PayloadStore.DeleteIfPresentAsync(payloadReference.PayloadBlobName, cancellationToken).ConfigureAwait(false);
            logger.LogDebug(
                exception,
                "Ignoring duplicate deterministic outbox enqueue for idempotency key {IdempotencyKey}.",
                idempotencyKey);
        }
    }

    public Task EnqueueAsync(string topic, string payload, IDbTransaction transaction, CancellationToken cancellationToken) =>
        EnqueueAsync(topic, payload, transaction, correlationId: null, dueTimeUtc: null, cancellationToken);

    public Task EnqueueAsync(
        string topic,
        string payload,
        IDbTransaction transaction,
        string? correlationId,
        CancellationToken cancellationToken) =>
        EnqueueAsync(topic, payload, transaction, correlationId, dueTimeUtc: null, cancellationToken);

    public Task EnqueueAsync(
        string topic,
        string payload,
        IDbTransaction transaction,
        string? correlationId,
        DateTimeOffset? dueTimeUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        throw new NotSupportedException(
            "Azure Storage outbox does not support IDbTransaction-bound enqueue operations. " +
            "Use the non-transactional Azure-native enqueue overloads instead.");
    }

    public async Task<IReadOnlyList<OutboxWorkItemIdentifier>> ClaimAsync(
        OwnerToken ownerToken,
        int leaseSeconds,
        int batchSize,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AzureOutboxRecordModel> claimed = await ClaimInternalAsync(ownerToken, leaseSeconds, batchSize, cancellationToken).ConfigureAwait(false);
        return claimed.Select(static record => OutboxWorkItemIdentifier.From(record.Id)).ToList();
    }

    public async Task AckAsync(
        OwnerToken ownerToken,
        IEnumerable<OutboxWorkItemIdentifier> ids,
        CancellationToken cancellationToken)
    {
        foreach (OutboxWorkItemIdentifier id in ids)
        {
            await CompleteAsync(id.Value, ownerToken, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AbandonAsync(
        OwnerToken ownerToken,
        IEnumerable<OutboxWorkItemIdentifier> ids,
        CancellationToken cancellationToken)
    {
        foreach (OutboxWorkItemIdentifier id in ids)
        {
            await RescheduleClaimedAsync(id.Value, ownerToken, lastError: null, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task FailAsync(
        OwnerToken ownerToken,
        IEnumerable<OutboxWorkItemIdentifier> ids,
        CancellationToken cancellationToken)
    {
        foreach (OutboxWorkItemIdentifier id in ids)
        {
            await FailClaimedAsync(id.Value, ownerToken, "Outbox item was failed without explicit error text.", cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ReapExpiredAsync(CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        string upperBound = AzurePlatformRowKeys.Lock(ToUnixMilliseconds(now), new string('~', 16));

        await foreach (TableEntity lockEntity in resources.Table.Client.QueryAsync<TableEntity>(
                           filter: $"PartitionKey eq '{AzurePlatformTableConstants.PartitionKey}' and RowKey ge 'lock|' and RowKey le '{upperBound}'",
                           maxPerPage: 100,
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            string itemId = ParseTrailingId(lockEntity.RowKey);
            NullableResponse<TableEntity> itemResponse = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.Item(itemId),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!itemResponse.HasValue)
            {
                await DeleteEntityBestEffortAsync(lockEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            AzureOutboxRecordModel record = Deserialize<AzureOutboxRecordModel>(itemResponse.Value!);
            if (record.Status != StatusInProgress ||
                !string.Equals(record.LockRowKey, lockEntity.RowKey, StringComparison.Ordinal) ||
                record.LockedUntilUtc is null ||
                record.LockedUntilUtc > now)
            {
                continue;
            }

            record.Status = StatusReady;
            record.OwnerToken = null;
            record.LockedUntilUtc = null;
            record.LockRowKey = null;
            record.DueTimeUtc = now;
            record.DueRowKey = AzurePlatformRowKeys.Due(ToUnixMilliseconds(now), itemId);

            List<TableTransactionAction> actions =
            [
                new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(itemResponse.Value!, record), itemResponse.Value!.ETag),
                new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag),
                new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(record.DueRowKey!, "OutboxDue", new AzureOutboxIndexModel { Id = record.Id })),
            ];

            try
            {
                await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
                await resources.SignalQueue.SendSignalAsync(SignalName, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Outbox reap for item {OutboxId} lost an optimistic concurrency race.", record.Id);
            }
        }
    }

    public async Task<JoinIdentifier> StartJoinAsync(
        long tenantId,
        int expectedSteps,
        string? metadata,
        CancellationToken cancellationToken)
    {
        OutboxJoin join = await joinStore.CreateJoinAsync(tenantId, expectedSteps, metadata, cancellationToken).ConfigureAwait(false);
        return join.JoinId;
    }

    public Task AttachMessageToJoinAsync(JoinIdentifier joinId, OutboxMessageIdentifier outboxMessageId, CancellationToken cancellationToken) =>
        joinStore.AttachMessageToJoinAsync(joinId, outboxMessageId, cancellationToken);

    public Task ReportStepCompletedAsync(JoinIdentifier joinId, OutboxMessageIdentifier outboxMessageId, CancellationToken cancellationToken) =>
        joinStore.IncrementCompletedAsync(joinId, outboxMessageId, cancellationToken);

    public Task ReportStepFailedAsync(JoinIdentifier joinId, OutboxMessageIdentifier outboxMessageId, CancellationToken cancellationToken) =>
        joinStore.IncrementFailedAsync(joinId, outboxMessageId, cancellationToken);

    internal async Task<IReadOnlyList<OutboxMessage>> ClaimDueMessagesAsync(
        OwnerToken ownerToken,
        int leaseSeconds,
        int batchSize,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AzureOutboxRecordModel> claimed = await ClaimInternalAsync(ownerToken, leaseSeconds, batchSize, cancellationToken).ConfigureAwait(false);
        List<OutboxMessage> messages = [];
        foreach (AzureOutboxRecordModel record in claimed)
        {
            messages.Add(await ToOutboxMessageAsync(record, cancellationToken).ConfigureAwait(false));
        }

        return messages;
    }

    internal Task MarkDispatchedAsync(Guid id, OwnerToken ownerToken, CancellationToken cancellationToken) =>
        CompleteAsync(id, ownerToken, cancellationToken);

    internal Task RescheduleAsync(Guid id, OwnerToken ownerToken, TimeSpan delay, string lastError, CancellationToken cancellationToken) =>
        RescheduleClaimedAsync(id, ownerToken, lastError, delay, cancellationToken);

    internal Task FailAsync(Guid id, OwnerToken ownerToken, string lastError, CancellationToken cancellationToken) =>
        FailClaimedAsync(id, ownerToken, lastError, cancellationToken);

    private async Task<IReadOnlyList<AzureOutboxRecordModel>> ClaimInternalAsync(
        OwnerToken ownerToken,
        int leaseSeconds,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset lockedUntil = now.AddSeconds(leaseSeconds);
        string upperBound = AzurePlatformRowKeys.Due(ToUnixMilliseconds(now), new string('~', 16));
        List<AzureOutboxRecordModel> claimed = [];

        await foreach (TableEntity dueEntity in resources.Table.Client.QueryAsync<TableEntity>(
                           filter: $"PartitionKey eq '{AzurePlatformTableConstants.PartitionKey}' and RowKey ge 'due|' and RowKey le '{upperBound}'",
                           maxPerPage: Math.Max(batchSize * 4, 20),
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (claimed.Count >= batchSize)
            {
                break;
            }

            string itemId = ParseTrailingId(dueEntity.RowKey);
            NullableResponse<TableEntity> itemResponse = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.Item(itemId),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!itemResponse.HasValue)
            {
                await DeleteEntityBestEffortAsync(dueEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            AzureOutboxRecordModel record = Deserialize<AzureOutboxRecordModel>(itemResponse.Value!);
            if (record.Status != StatusReady ||
                record.DueTimeUtc is null ||
                record.DueTimeUtc > now ||
                !string.Equals(record.DueRowKey, dueEntity.RowKey, StringComparison.Ordinal))
            {
                await CleanupStaleDueRowAsync(record, dueEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            record.Status = StatusInProgress;
            record.OwnerToken = ownerToken.Value.ToString("N");
            record.LockedUntilUtc = lockedUntil;
            record.DueRowKey = null;
            record.LockRowKey = AzurePlatformRowKeys.Lock(ToUnixMilliseconds(lockedUntil), itemId);

            List<TableTransactionAction> actions =
            [
                new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(itemResponse.Value!, record), itemResponse.Value!.ETag),
                new TableTransactionAction(TableTransactionActionType.Delete, dueEntity, dueEntity.ETag),
                new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(record.LockRowKey!, "OutboxLock", new AzureOutboxIndexModel { Id = record.Id })),
            ];

            try
            {
                await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
                claimed.Add(record);
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Outbox claim for item {OutboxId} lost an optimistic concurrency race.", record.Id);
            }
        }

        return claimed;
    }

    private async Task CompleteAsync(Guid id, OwnerToken ownerToken, CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        NullableResponse<TableEntity> itemResponse = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Item(id.ToString("N")),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!itemResponse.HasValue)
        {
            return;
        }

        AzureOutboxRecordModel record = Deserialize<AzureOutboxRecordModel>(itemResponse.Value!);
        if (!IsOwnedBy(record, ownerToken) || record.Status != StatusInProgress)
        {
            return;
        }

        string? lockRowKey = record.LockRowKey;
        record.Status = StatusDone;
        record.OwnerToken = null;
        record.LockedUntilUtc = null;
        record.LockRowKey = null;
        record.ProcessedAt = now;
        record.ProcessedBy = Environment.MachineName;

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(itemResponse.Value!, record), itemResponse.Value!.ETag),
        ];

        TableEntity? lockEntity = await TryGetEntityAsync(lockRowKey, cancellationToken).ConfigureAwait(false);
        if (lockEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag));
        }

        await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
    }

    private async Task RescheduleClaimedAsync(
        Guid id,
        OwnerToken ownerToken,
        string? lastError,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay must be non-negative.");
        }

        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset nextDue = now.Add(delay);
        NullableResponse<TableEntity> itemResponse = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Item(id.ToString("N")),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!itemResponse.HasValue)
        {
            return;
        }

        AzureOutboxRecordModel record = Deserialize<AzureOutboxRecordModel>(itemResponse.Value!);
        if (!IsOwnedBy(record, ownerToken) || record.Status != StatusInProgress)
        {
            return;
        }

        string? lockRowKey = record.LockRowKey;
        record.Status = StatusReady;
        record.OwnerToken = null;
        record.LockedUntilUtc = null;
        record.LockRowKey = null;
        record.RetryCount += 1;
        if (!string.IsNullOrWhiteSpace(lastError))
        {
            record.LastError = lastError;
        }

        record.DueTimeUtc = nextDue;
        record.DueRowKey = AzurePlatformRowKeys.Due(ToUnixMilliseconds(nextDue), id.ToString("N"));

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(itemResponse.Value!, record), itemResponse.Value!.ETag),
            new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(record.DueRowKey!, "OutboxDue", new AzureOutboxIndexModel { Id = record.Id })),
        ];

        TableEntity? lockEntity = await TryGetEntityAsync(lockRowKey, cancellationToken).ConfigureAwait(false);
        if (lockEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag));
        }

        await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
        await SignalIfReadyAsync(nextDue, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task FailClaimedAsync(Guid id, OwnerToken ownerToken, string lastError, CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        NullableResponse<TableEntity> itemResponse = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Item(id.ToString("N")),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!itemResponse.HasValue)
        {
            return;
        }

        AzureOutboxRecordModel record = Deserialize<AzureOutboxRecordModel>(itemResponse.Value!);
        if (!IsOwnedBy(record, ownerToken) || record.Status != StatusInProgress)
        {
            return;
        }

        string? lockRowKey = record.LockRowKey;
        record.Status = StatusFailed;
        record.OwnerToken = null;
        record.LockedUntilUtc = null;
        record.LockRowKey = null;
        record.LastError = lastError;
        record.ProcessedBy = $"{Environment.MachineName}:FAILED";

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(itemResponse.Value!, record), itemResponse.Value!.ETag),
        ];

        TableEntity? lockEntity = await TryGetEntityAsync(lockRowKey, cancellationToken).ConfigureAwait(false);
        if (lockEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag));
        }

        await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
    }

    private async Task CleanupStaleDueRowAsync(AzureOutboxRecordModel record, TableEntity dueEntity, CancellationToken cancellationToken)
    {
        if (record.Status == StatusReady &&
            string.Equals(record.DueRowKey, dueEntity.RowKey, StringComparison.Ordinal))
        {
            return;
        }

        await DeleteEntityBestEffortAsync(dueEntity, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteEntityBestEffortAsync(TableEntity entity, CancellationToken cancellationToken)
    {
        try
        {
            await resources.Table.Client.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, entity.ETag, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception) || AzurePlatformExceptionHelper.IsNotFound(exception))
        {
            logger.LogDebug(exception, "Ignoring best-effort outbox cleanup race for row {RowKey}.", entity.RowKey);
        }
    }

    private async Task<TableEntity?> TryGetEntityAsync(string? rowKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rowKey))
        {
            return null;
        }

        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                rowKey,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.HasValue ? response.Value : null;
    }

    private async Task<OutboxMessage> ToOutboxMessageAsync(AzureOutboxRecordModel record, CancellationToken cancellationToken)
    {
        string payload = await resources.PayloadStore.ReadTextAsync(record.Payload, cancellationToken).ConfigureAwait(false);
        return new OutboxMessage
        {
            Id = OutboxWorkItemIdentifier.From(record.Id),
            MessageId = OutboxMessageIdentifier.From(record.MessageId),
            Topic = record.Topic,
            Payload = payload,
            CreatedAt = record.CreatedAt,
            IsProcessed = record.Status == StatusDone,
            ProcessedAt = record.ProcessedAt,
            ProcessedBy = record.ProcessedBy,
            RetryCount = record.RetryCount,
            LastError = record.LastError,
            CorrelationId = record.CorrelationId,
            DueTimeUtc = record.DueTimeUtc,
        };
    }

    private async Task SignalIfReadyAsync(DateTimeOffset dueTimeUtc, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (dueTimeUtc <= now.AddSeconds(1))
        {
            await resources.SignalQueue.SendSignalAsync(SignalName, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> MatchesExistingDeterministicMessageAsync(
        Guid workItemId,
        string topic,
        string? correlationId,
        AzurePayloadReference payloadReference,
        CancellationToken cancellationToken)
    {
        NullableResponse<TableEntity> existingResponse = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Item(workItemId.ToString("N")),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!existingResponse.HasValue)
        {
            return false;
        }

        AzureOutboxRecordModel existing = Deserialize<AzureOutboxRecordModel>(existingResponse.Value!);
        return string.Equals(existing.Topic, topic, StringComparison.Ordinal) &&
               string.Equals(existing.CorrelationId, correlationId, StringComparison.Ordinal) &&
               string.Equals(existing.Payload.PayloadChecksum, payloadReference.PayloadChecksum, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOwnedBy(AzureOutboxRecordModel record, OwnerToken ownerToken) =>
        string.Equals(record.OwnerToken, ownerToken.Value.ToString("N"), StringComparison.OrdinalIgnoreCase);

    private TableEntity CreateEntity(string rowKey, string entityType, object model)
    {
        return new TableEntity(AzurePlatformTableConstants.PartitionKey, rowKey)
        {
            [AzurePlatformTableConstants.EntityTypeProperty] = entityType,
            [AzurePlatformTableConstants.DataProperty] = resources.Serializer.SerializeToString(model),
        };
    }

    private TableEntity CreateEntity(TableEntity currentEntity, object model)
    {
        return new TableEntity(currentEntity.PartitionKey, currentEntity.RowKey)
        {
            [AzurePlatformTableConstants.EntityTypeProperty] = currentEntity.GetString(AzurePlatformTableConstants.EntityTypeProperty),
            [AzurePlatformTableConstants.DataProperty] = resources.Serializer.SerializeToString(model),
        };
    }

    private T Deserialize<T>(TableEntity entity)
    {
        string json = entity.GetString(AzurePlatformTableConstants.DataProperty)
            ?? throw new InvalidOperationException($"Azure Table entity '{entity.RowKey}' does not contain serialized data.");
        return resources.Serializer.Deserialize<T>(json)
            ?? throw new InvalidOperationException($"Azure Table entity '{entity.RowKey}' could not be deserialized as {typeof(T).Name}.");
    }

    private static string ParseTrailingId(string rowKey)
    {
        int separatorIndex = rowKey.LastIndexOf('|');
        return separatorIndex >= 0 ? rowKey[(separatorIndex + 1)..] : rowKey;
    }

    private static long ToUnixMilliseconds(DateTimeOffset value) => value.ToUniversalTime().ToUnixTimeMilliseconds();
}

internal sealed class AzureOutboxStore : IOutboxStore
{
    private readonly AzureOutboxService outbox;
    private readonly OwnerToken ownerToken = OwnerToken.GenerateNew();
    private readonly AzurePlatformOptions options;

    public AzureOutboxStore(AzureOutboxService outbox, AzurePlatformOptions options)
    {
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(int limit, CancellationToken cancellationToken)
    {
        int leaseSeconds = Math.Max(1, (int)Math.Ceiling(options.ClaimLeaseDuration.TotalSeconds));
        return outbox.ClaimDueMessagesAsync(ownerToken, leaseSeconds, limit, cancellationToken);
    }

    public Task MarkDispatchedAsync(OutboxWorkItemIdentifier id, CancellationToken cancellationToken) =>
        outbox.MarkDispatchedAsync(id.Value, ownerToken, cancellationToken);

    public Task RescheduleAsync(OutboxWorkItemIdentifier id, TimeSpan delay, string lastError, CancellationToken cancellationToken) =>
        outbox.RescheduleAsync(id.Value, ownerToken, delay, lastError, cancellationToken);

    public Task FailAsync(OutboxWorkItemIdentifier id, string lastError, CancellationToken cancellationToken) =>
        outbox.FailAsync(id.Value, ownerToken, lastError, cancellationToken);
}
