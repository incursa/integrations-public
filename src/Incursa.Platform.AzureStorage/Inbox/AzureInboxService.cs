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

using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace Incursa.Platform;

internal sealed class AzureInboxService : IInbox
{
    private const byte StatusSeen = 0;
    private const byte StatusProcessing = 1;
    private const byte StatusDone = 2;
    private const byte StatusDead = 3;
    private const string SignalName = "inbox-ready";

    private readonly AzureInboxResources resources;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AzureInboxService> logger;

    public AzureInboxService(
        AzureInboxResources resources,
        TimeProvider timeProvider,
        ILogger<AzureInboxService> logger)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal AzureInboxResources Resources => resources;

    public Task<bool> AlreadyProcessedAsync(string messageId, string source, CancellationToken cancellationToken) =>
        AlreadyProcessedAsync(messageId, source, hash: null, cancellationToken);

    public async Task<bool> AlreadyProcessedAsync(
        string messageId,
        string source,
        byte[]? hash,
        CancellationToken cancellationToken)
    {
        ValidateMessageIdentity(messageId, source);
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        string encodedId = EncodeMessageId(messageId);
        string itemRowKey = AzurePlatformRowKeys.Item(encodedId);
        string? hashBase64 = EncodeHash(hash);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    itemRowKey,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.HasValue)
            {
                AzureInboxRecordModel record = new()
                {
                    MessageId = messageId,
                    Source = source,
                    HashBase64 = hashBase64,
                    Attempt = 1,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                    Status = StatusSeen,
                    Payload = new AzurePayloadReference(null, null, null),
                };

                try
                {
                    await resources.Table.Client.AddEntityAsync(CreateEntity(itemRowKey, "InboxItem", record), cancellationToken).ConfigureAwait(false);
                    return false;
                }
                catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
                {
                    logger.LogDebug(exception, "Inbox already-processed insert for message {MessageId} lost an optimistic concurrency race.", messageId);
                    continue;
                }
            }

            AzureInboxRecordModel current = Deserialize<AzureInboxRecordModel>(response.Value!);
            LogIdentityMismatchIfAny(current, source, hashBase64);
            current.Attempt = checked(current.Attempt + 1);
            current.LastSeenUtc = now;
            if (current.HashBase64 is null && hashBase64 is not null)
            {
                current.HashBase64 = hashBase64;
            }

            try
            {
                await resources.Table.Client.UpdateEntityAsync(
                        CreateEntity(response.Value!, current),
                        response.Value!.ETag,
                        TableUpdateMode.Replace,
                        cancellationToken)
                    .ConfigureAwait(false);
                return IsProcessed(current);
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Inbox already-processed update for message {MessageId} lost an optimistic concurrency race.", messageId);
            }
        }

        throw new InvalidOperationException($"Inbox message '{messageId}' could not be read or updated after repeated optimistic concurrency retries.");
    }

    public Task EnqueueAsync(string topic, string source, string messageId, string payload, CancellationToken cancellationToken) =>
        EnqueueAsync(topic, source, messageId, payload, hash: null, dueTimeUtc: null, cancellationToken);

    public Task EnqueueAsync(
        string topic,
        string source,
        string messageId,
        string payload,
        byte[]? hash,
        CancellationToken cancellationToken) =>
        EnqueueAsync(topic, source, messageId, payload, hash, dueTimeUtc: null, cancellationToken);

    public async Task EnqueueAsync(
        string topic,
        string source,
        string messageId,
        string payload,
        byte[]? hash,
        DateTimeOffset? dueTimeUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ValidateMessageIdentity(messageId, source);
        ArgumentNullException.ThrowIfNull(payload);

        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        string encodedId = EncodeMessageId(messageId);
        string itemRowKey = AzurePlatformRowKeys.Item(encodedId);
        string? hashBase64 = EncodeHash(hash);
        AzurePayloadReference payloadReference = await resources.PayloadStore
            .StoreTextAsync("inbox", encodedId, payload, cancellationToken)
            .ConfigureAwait(false);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            DateTimeOffset effectiveDue = NormalizeDueTime(dueTimeUtc ?? now);
            NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    itemRowKey,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.HasValue)
            {
                AzureInboxRecordModel record = new()
                {
                    MessageId = messageId,
                    Source = source,
                    Topic = topic,
                    Payload = payloadReference,
                    HashBase64 = hashBase64,
                    Attempt = 1,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                    DueTimeUtc = effectiveDue,
                    Status = StatusSeen,
                    DueRowKey = AzurePlatformRowKeys.Due(ToUnixMilliseconds(effectiveDue), encodedId),
                };

                try
                {
                    await resources.Table.Client.SubmitTransactionAsync(
                            [
                                new TableTransactionAction(TableTransactionActionType.Add, CreateEntity(itemRowKey, "InboxItem", record)),
                                new TableTransactionAction(
                                    TableTransactionActionType.UpsertReplace,
                                    CreateEntity(record.DueRowKey!, "InboxDue", new AzureInboxIndexModel { MessageId = messageId })),
                            ],
                            cancellationToken)
                        .ConfigureAwait(false);
                    await SignalIfReadyAsync(record.DueTimeUtc!.Value, now, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
                {
                    logger.LogDebug(exception, "Inbox enqueue insert for message {MessageId} lost an optimistic concurrency race.", messageId);
                    continue;
                }
            }

            AzureInboxRecordModel current = Deserialize<AzureInboxRecordModel>(response.Value!);
            LogIdentityMismatchIfAny(current, source, hashBase64);

            AzurePayloadReference previousPayload = current.Payload;
            current.Attempt = checked(current.Attempt + 1);
            current.LastSeenUtc = now;
            current.Topic = topic;
            current.Payload = payloadReference;
            if (current.HashBase64 is null && hashBase64 is not null)
            {
                current.HashBase64 = hashBase64;
            }

            List<TableTransactionAction> actions =
            [
                new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, current), response.Value!.ETag),
            ];

            if (current.Status == StatusSeen)
            {
                DateTimeOffset due = NormalizeDueTime(dueTimeUtc ?? current.DueTimeUtc ?? now);
                current.DueTimeUtc = due;
                current.DueRowKey = AzurePlatformRowKeys.Due(ToUnixMilliseconds(due), encodedId);
                actions[0] = new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, current), response.Value!.ETag);
                actions.Add(
                    new TableTransactionAction(
                        TableTransactionActionType.UpsertReplace,
                        CreateEntity(current.DueRowKey, "InboxDue", new AzureInboxIndexModel { MessageId = messageId })));
            }
            else if (dueTimeUtc.HasValue)
            {
                current.DueTimeUtc = effectiveDue;
                actions[0] = new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, current), response.Value!.ETag);
            }

            try
            {
                await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
                await CleanupReplacedPayloadAsync(previousPayload, current.Payload, cancellationToken).ConfigureAwait(false);
                if (current.Status == StatusSeen && current.DueTimeUtc is not null)
                {
                    await SignalIfReadyAsync(current.DueTimeUtc.Value, now, cancellationToken).ConfigureAwait(false);
                }

                return;
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Inbox enqueue update for message {MessageId} lost an optimistic concurrency race.", messageId);
            }
        }

        throw new InvalidOperationException($"Inbox message '{messageId}' could not be enqueued after repeated optimistic concurrency retries.");
    }

    public Task MarkProcessedAsync(string messageId, CancellationToken cancellationToken) =>
        UpdateStatusAsync(messageId, StatusDone, "processed", cancellationToken);

    public Task MarkProcessingAsync(string messageId, CancellationToken cancellationToken) =>
        UpdateStatusAsync(messageId, StatusProcessing, "processing", cancellationToken);

    public Task MarkDeadAsync(string messageId, CancellationToken cancellationToken) =>
        UpdateStatusAsync(messageId, StatusDead, "dead", cancellationToken);

    internal async Task<IReadOnlyList<string>> ClaimAsync(
        OwnerToken ownerToken,
        int leaseSeconds,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset lockedUntil = now.AddSeconds(leaseSeconds);
        string upperBound = AzurePlatformRowKeys.Due(ToUnixMilliseconds(now), "~");
        List<string> claimed = [];

        await foreach (TableEntity dueEntity in resources.Table.Client.QueryAsync<TableEntity>(
                           filter: $"PartitionKey eq '{AzurePlatformTableConstants.PartitionKey}' and RowKey ge 'due|' and RowKey le '{upperBound}'",
                           maxPerPage: Math.Max(batchSize * 4, 20),
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (claimed.Count >= batchSize)
            {
                break;
            }

            string encodedId = ParseTrailingId(dueEntity.RowKey);
            NullableResponse<TableEntity> itemResponse = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.Item(encodedId),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!itemResponse.HasValue)
            {
                await DeleteEntityBestEffortAsync(dueEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            AzureInboxRecordModel record = Deserialize<AzureInboxRecordModel>(itemResponse.Value!);
            if (record.Status != StatusSeen ||
                record.DueTimeUtc is null ||
                record.DueTimeUtc > now ||
                !string.Equals(record.DueRowKey, dueEntity.RowKey, StringComparison.Ordinal))
            {
                await CleanupStaleDueRowAsync(record, dueEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            record.Status = StatusProcessing;
            record.OwnerToken = ownerToken.Value.ToString("N");
            record.LockedUntilUtc = lockedUntil;
            record.DueRowKey = null;
            record.LockRowKey = AzurePlatformRowKeys.Lock(ToUnixMilliseconds(lockedUntil), encodedId);

            try
            {
                await resources.Table.Client.SubmitTransactionAsync(
                        [
                            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(itemResponse.Value!, record), itemResponse.Value!.ETag),
                            new TableTransactionAction(TableTransactionActionType.Delete, dueEntity, dueEntity.ETag),
                            new TableTransactionAction(
                                TableTransactionActionType.Add,
                                CreateEntity(record.LockRowKey, "InboxLock", new AzureInboxIndexModel { MessageId = record.MessageId })),
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
                claimed.Add(record.MessageId);
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Inbox claim for message {MessageId} lost an optimistic concurrency race.", record.MessageId);
            }
        }

        return claimed;
    }

    internal async Task AckAsync(OwnerToken ownerToken, IEnumerable<string> messageIds, CancellationToken cancellationToken)
    {
        foreach (string messageId in messageIds)
        {
            await CompleteAsync(messageId, ownerToken, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task AbandonAsync(
        OwnerToken ownerToken,
        IEnumerable<string> messageIds,
        string? lastError,
        TimeSpan? delay,
        CancellationToken cancellationToken)
    {
        if (delay.HasValue && delay.Value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay must be non-negative.");
        }

        foreach (string messageId in messageIds)
        {
            await RescheduleClaimedAsync(messageId, ownerToken, lastError, delay ?? TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task FailAsync(
        OwnerToken ownerToken,
        IEnumerable<string> messageIds,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        foreach (string messageId in messageIds)
        {
            await FailClaimedAsync(messageId, ownerToken, errorMessage, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task ReviveAsync(
        IEnumerable<string> messageIds,
        string? reason,
        TimeSpan? delay,
        CancellationToken cancellationToken)
    {
        if (delay.HasValue && delay.Value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay must be non-negative.");
        }

        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset dueTime = NormalizeDueTime(now.Add(delay ?? TimeSpan.Zero));
        string? normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        foreach (string messageId in messageIds)
        {
            string encodedId = EncodeMessageId(messageId);
            NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.Item(encodedId),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.HasValue)
            {
                continue;
            }

            AzureInboxRecordModel record = Deserialize<AzureInboxRecordModel>(response.Value!);
            if (record.Status != StatusDead)
            {
                continue;
            }

            string? lockRowKey = record.LockRowKey;
            record.Status = StatusSeen;
            record.OwnerToken = null;
            record.LockedUntilUtc = null;
            record.LockRowKey = null;
            record.DueTimeUtc = dueTime;
            record.DueRowKey = AzurePlatformRowKeys.Due(ToUnixMilliseconds(dueTime), encodedId);
            record.LastSeenUtc = now;
            record.LastError = normalizedReason;

            List<TableTransactionAction> actions =
            [
                new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, record), response.Value!.ETag),
                new TableTransactionAction(
                    TableTransactionActionType.UpsertReplace,
                    CreateEntity(record.DueRowKey, "InboxDue", new AzureInboxIndexModel { MessageId = record.MessageId })),
            ];

            TableEntity? lockEntity = await TryGetEntityAsync(lockRowKey, cancellationToken).ConfigureAwait(false);
            if (lockEntity is not null)
            {
                actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag));
            }

            try
            {
                await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
                await SignalIfReadyAsync(dueTime, now, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Inbox revive for message {MessageId} lost an optimistic concurrency race.", messageId);
            }
        }
    }

    internal async Task ReapExpiredAsync(CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        string upperBound = AzurePlatformRowKeys.Lock(ToUnixMilliseconds(now), "~");

        await foreach (TableEntity lockEntity in resources.Table.Client.QueryAsync<TableEntity>(
                           filter: $"PartitionKey eq '{AzurePlatformTableConstants.PartitionKey}' and RowKey ge 'lock|' and RowKey le '{upperBound}'",
                           maxPerPage: 100,
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            string encodedId = ParseTrailingId(lockEntity.RowKey);
            NullableResponse<TableEntity> itemResponse = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.Item(encodedId),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!itemResponse.HasValue)
            {
                await DeleteEntityBestEffortAsync(lockEntity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            AzureInboxRecordModel record = Deserialize<AzureInboxRecordModel>(itemResponse.Value!);
            if (record.Status != StatusProcessing ||
                !string.Equals(record.LockRowKey, lockEntity.RowKey, StringComparison.Ordinal) ||
                record.LockedUntilUtc is null ||
                record.LockedUntilUtc > now)
            {
                continue;
            }

            record.Status = StatusSeen;
            record.OwnerToken = null;
            record.LockedUntilUtc = null;
            record.LockRowKey = null;
            record.DueTimeUtc = now;
            record.DueRowKey = AzurePlatformRowKeys.Due(ToUnixMilliseconds(now), encodedId);

            try
            {
                await resources.Table.Client.SubmitTransactionAsync(
                        [
                            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(itemResponse.Value!, record), itemResponse.Value!.ETag),
                            new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag),
                            new TableTransactionAction(
                                TableTransactionActionType.UpsertReplace,
                                CreateEntity(record.DueRowKey, "InboxDue", new AzureInboxIndexModel { MessageId = record.MessageId })),
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
                await resources.SignalQueue.SendSignalAsync(SignalName, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Inbox reap for message {MessageId} lost an optimistic concurrency race.", record.MessageId);
            }
        }
    }

    internal async Task<InboxMessage> GetAsync(string messageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Item(EncodeMessageId(messageId)),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            throw new InvalidOperationException($"Inbox message '{messageId}' not found.");
        }

        AzureInboxRecordModel record = Deserialize<AzureInboxRecordModel>(response.Value!);
        string payload = await resources.PayloadStore.ReadTextAsync(record.Payload, cancellationToken).ConfigureAwait(false);

        return new InboxMessage
        {
            MessageId = record.MessageId,
            Source = record.Source,
            Topic = record.Topic,
            Payload = payload,
            Hash = DecodeHash(record.HashBase64),
            Attempt = record.Attempt,
            FirstSeenUtc = record.FirstSeenUtc,
            LastSeenUtc = record.LastSeenUtc,
            DueTimeUtc = record.DueTimeUtc,
            LastError = record.LastError,
        };
    }

    private async Task UpdateStatusAsync(string messageId, byte status, string statusName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        string encodedId = EncodeMessageId(messageId);
        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Item(encodedId),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        AzureInboxRecordModel record = Deserialize<AzureInboxRecordModel>(response.Value!);
        string? dueRowKey = record.DueRowKey;
        string? lockRowKey = record.LockRowKey;

        record.Status = status;
        record.LastSeenUtc = now;
        record.OwnerToken = null;
        record.LockedUntilUtc = null;
        record.LockRowKey = null;
        record.DueRowKey = null;
        if (status == StatusDone)
        {
            record.ProcessedUtc = now;
        }

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, record), response.Value!.ETag),
        ];

        TableEntity? dueEntity = await TryGetEntityAsync(dueRowKey, cancellationToken).ConfigureAwait(false);
        if (dueEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, dueEntity, dueEntity.ETag));
        }

        TableEntity? lockEntity = await TryGetEntityAsync(lockRowKey, cancellationToken).ConfigureAwait(false);
        if (lockEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag));
        }

        try
        {
            await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
        {
            logger.LogDebug(exception, "Inbox message {MessageId} lost an optimistic concurrency race while being marked {Status}.", messageId, statusName);
        }
    }

    private async Task CompleteAsync(string messageId, OwnerToken ownerToken, CancellationToken cancellationToken)
    {
        string encodedId = EncodeMessageId(messageId);
        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Item(encodedId),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return;
        }

        AzureInboxRecordModel record = Deserialize<AzureInboxRecordModel>(response.Value!);
        if (!IsOwnedBy(record, ownerToken) || record.Status != StatusProcessing)
        {
            return;
        }

        string? lockRowKey = record.LockRowKey;
        record.Status = StatusDone;
        record.OwnerToken = null;
        record.LockedUntilUtc = null;
        record.LockRowKey = null;
        record.ProcessedUtc = timeProvider.GetUtcNow();
        record.LastError = null;

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, record), response.Value!.ETag),
        ];

        TableEntity? lockEntity = await TryGetEntityAsync(lockRowKey, cancellationToken).ConfigureAwait(false);
        if (lockEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag));
        }

        await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
    }

    private async Task RescheduleClaimedAsync(
        string messageId,
        OwnerToken ownerToken,
        string? lastError,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        string encodedId = EncodeMessageId(messageId);
        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Item(encodedId),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset nextDue = NormalizeDueTime(now.Add(delay));
        AzureInboxRecordModel record = Deserialize<AzureInboxRecordModel>(response.Value!);
        if (!IsOwnedBy(record, ownerToken) || record.Status != StatusProcessing)
        {
            return;
        }

        string? lockRowKey = record.LockRowKey;
        record.Status = StatusSeen;
        record.OwnerToken = null;
        record.LockedUntilUtc = null;
        record.LockRowKey = null;
        record.Attempt = checked(record.Attempt + 1);
        record.DueTimeUtc = nextDue;
        record.DueRowKey = AzurePlatformRowKeys.Due(ToUnixMilliseconds(nextDue), encodedId);
        if (!string.IsNullOrWhiteSpace(lastError))
        {
            record.LastError = lastError;
        }

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, record), response.Value!.ETag),
            new TableTransactionAction(
                TableTransactionActionType.UpsertReplace,
                CreateEntity(record.DueRowKey, "InboxDue", new AzureInboxIndexModel { MessageId = record.MessageId })),
        ];

        TableEntity? lockEntity = await TryGetEntityAsync(lockRowKey, cancellationToken).ConfigureAwait(false);
        if (lockEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag));
        }

        await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
        await SignalIfReadyAsync(nextDue, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task FailClaimedAsync(string messageId, OwnerToken ownerToken, string lastError, CancellationToken cancellationToken)
    {
        string encodedId = EncodeMessageId(messageId);
        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Item(encodedId),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return;
        }

        AzureInboxRecordModel record = Deserialize<AzureInboxRecordModel>(response.Value!);
        if (!IsOwnedBy(record, ownerToken) || record.Status != StatusProcessing)
        {
            return;
        }

        string? lockRowKey = record.LockRowKey;
        record.Status = StatusDead;
        record.OwnerToken = null;
        record.LockedUntilUtc = null;
        record.LockRowKey = null;
        record.LastError = lastError;

        List<TableTransactionAction> actions =
        [
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(response.Value!, record), response.Value!.ETag),
        ];

        TableEntity? lockEntity = await TryGetEntityAsync(lockRowKey, cancellationToken).ConfigureAwait(false);
        if (lockEntity is not null)
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, lockEntity, lockEntity.ETag));
        }

        await resources.Table.Client.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
    }

    private async Task CleanupStaleDueRowAsync(AzureInboxRecordModel record, TableEntity dueEntity, CancellationToken cancellationToken)
    {
        if (record.Status == StatusSeen &&
            string.Equals(record.DueRowKey, dueEntity.RowKey, StringComparison.Ordinal))
        {
            return;
        }

        await DeleteEntityBestEffortAsync(dueEntity, cancellationToken).ConfigureAwait(false);
    }

    private async Task CleanupReplacedPayloadAsync(
        AzurePayloadReference previousPayload,
        AzurePayloadReference currentPayload,
        CancellationToken cancellationToken)
    {
        if (string.Equals(previousPayload.PayloadBlobName, currentPayload.PayloadBlobName, StringComparison.Ordinal))
        {
            return;
        }

        await resources.PayloadStore.DeleteIfPresentAsync(previousPayload.PayloadBlobName, cancellationToken).ConfigureAwait(false);
    }

    private async Task SignalIfReadyAsync(DateTimeOffset dueTimeUtc, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (dueTimeUtc <= now.AddSeconds(1))
        {
            await resources.SignalQueue.SendSignalAsync(SignalName, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeleteEntityBestEffortAsync(TableEntity entity, CancellationToken cancellationToken)
    {
        try
        {
            await resources.Table.Client.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, entity.ETag, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception) || AzurePlatformExceptionHelper.IsNotFound(exception))
        {
            logger.LogDebug(exception, "Ignoring best-effort inbox cleanup race for row {RowKey}.", entity.RowKey);
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

    private static void ValidateMessageIdentity(string messageId, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
    }

    private static bool IsProcessed(AzureInboxRecordModel record) =>
        record.Status == StatusDone || record.ProcessedUtc is not null;

    private static bool IsOwnedBy(AzureInboxRecordModel record, OwnerToken ownerToken) =>
        string.Equals(record.OwnerToken, ownerToken.Value.ToString("N"), StringComparison.OrdinalIgnoreCase);

    private static string EncodeMessageId(string messageId) =>
        AzurePlatformNameResolver.EncodeKey(messageId);

    private static string? EncodeHash(byte[]? hash) =>
        hash is null || hash.Length == 0 ? null : Convert.ToBase64String(hash);

    private static byte[]? DecodeHash(string? hashBase64) =>
        string.IsNullOrWhiteSpace(hashBase64) ? null : Convert.FromBase64String(hashBase64);

    private static DateTimeOffset NormalizeDueTime(DateTimeOffset value) => value.ToUniversalTime();

    private void LogIdentityMismatchIfAny(AzureInboxRecordModel current, string source, string? hashBase64)
    {
        if (!string.Equals(current.Source, source, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Inbox message {MessageId} was previously recorded for source {ExistingSource} and is now being observed for source {IncomingSource}. Preserving the original source.",
                current.MessageId,
                current.Source,
                source);
        }

        if (hashBase64 is not null &&
            current.HashBase64 is not null &&
            !string.Equals(current.HashBase64, hashBase64, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Inbox message {MessageId} was previously recorded with a different hash. Preserving the original hash and keeping the message idempotent by message identifier.",
                current.MessageId);
        }
    }

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

internal sealed class AzureInboxWorkStore : IInboxWorkStore
{
    private readonly AzureInboxService inbox;

    public AzureInboxWorkStore(AzureInboxService inbox)
    {
        this.inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
    }

    public Task<IReadOnlyList<string>> ClaimAsync(OwnerToken ownerToken, int leaseSeconds, int batchSize, CancellationToken cancellationToken) =>
        inbox.ClaimAsync(ownerToken, leaseSeconds, batchSize, cancellationToken);

    public Task AckAsync(OwnerToken ownerToken, IEnumerable<string> messageIds, CancellationToken cancellationToken) =>
        inbox.AckAsync(ownerToken, messageIds, cancellationToken);

    public Task AbandonAsync(OwnerToken ownerToken, IEnumerable<string> messageIds, string? lastError = null, TimeSpan? delay = null, CancellationToken cancellationToken = default) =>
        inbox.AbandonAsync(ownerToken, messageIds, lastError, delay, cancellationToken);

    public Task FailAsync(OwnerToken ownerToken, IEnumerable<string> messageIds, string errorMessage, CancellationToken cancellationToken) =>
        inbox.FailAsync(ownerToken, messageIds, errorMessage, cancellationToken);

    public Task ReviveAsync(IEnumerable<string> messageIds, string? reason = null, TimeSpan? delay = null, CancellationToken cancellationToken = default) =>
        inbox.ReviveAsync(messageIds, reason, delay, cancellationToken);

    public Task ReapExpiredAsync(CancellationToken cancellationToken) =>
        inbox.ReapExpiredAsync(cancellationToken);

    public Task<InboxMessage> GetAsync(string messageId, CancellationToken cancellationToken) =>
        inbox.GetAsync(messageId, cancellationToken);
}

internal sealed class AzureGlobalInbox : IGlobalInbox
{
    private readonly IInbox inner;

    public AzureGlobalInbox(IInbox inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task<bool> AlreadyProcessedAsync(string messageId, string source, CancellationToken cancellationToken) =>
        inner.AlreadyProcessedAsync(messageId, source, cancellationToken);

    public Task<bool> AlreadyProcessedAsync(string messageId, string source, byte[]? hash, CancellationToken cancellationToken) =>
        inner.AlreadyProcessedAsync(messageId, source, hash, cancellationToken);

    public Task MarkProcessedAsync(string messageId, CancellationToken cancellationToken) =>
        inner.MarkProcessedAsync(messageId, cancellationToken);

    public Task MarkProcessingAsync(string messageId, CancellationToken cancellationToken) =>
        inner.MarkProcessingAsync(messageId, cancellationToken);

    public Task MarkDeadAsync(string messageId, CancellationToken cancellationToken) =>
        inner.MarkDeadAsync(messageId, cancellationToken);

    public Task EnqueueAsync(string topic, string source, string messageId, string payload, CancellationToken cancellationToken) =>
        inner.EnqueueAsync(topic, source, messageId, payload, cancellationToken);

    public Task EnqueueAsync(string topic, string source, string messageId, string payload, byte[]? hash, CancellationToken cancellationToken) =>
        inner.EnqueueAsync(topic, source, messageId, payload, hash, cancellationToken);

    public Task EnqueueAsync(
        string topic,
        string source,
        string messageId,
        string payload,
        byte[]? hash,
        DateTimeOffset? dueTimeUtc,
        CancellationToken cancellationToken) =>
        inner.EnqueueAsync(topic, source, messageId, payload, hash, dueTimeUtc, cancellationToken);
}

internal sealed class AzureGlobalInboxWorkStore : IGlobalInboxWorkStore
{
    private readonly IInboxWorkStore inner;

    public AzureGlobalInboxWorkStore(IInboxWorkStore inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task<IReadOnlyList<string>> ClaimAsync(OwnerToken ownerToken, int leaseSeconds, int batchSize, CancellationToken cancellationToken) =>
        inner.ClaimAsync(ownerToken, leaseSeconds, batchSize, cancellationToken);

    public Task AckAsync(OwnerToken ownerToken, IEnumerable<string> messageIds, CancellationToken cancellationToken) =>
        inner.AckAsync(ownerToken, messageIds, cancellationToken);

    public Task AbandonAsync(OwnerToken ownerToken, IEnumerable<string> messageIds, string? lastError = null, TimeSpan? delay = null, CancellationToken cancellationToken = default) =>
        inner.AbandonAsync(ownerToken, messageIds, lastError, delay, cancellationToken);

    public Task FailAsync(OwnerToken ownerToken, IEnumerable<string> messageIds, string errorMessage, CancellationToken cancellationToken) =>
        inner.FailAsync(ownerToken, messageIds, errorMessage, cancellationToken);

    public Task ReviveAsync(IEnumerable<string> messageIds, string? reason = null, TimeSpan? delay = null, CancellationToken cancellationToken = default) =>
        inner.ReviveAsync(messageIds, reason, delay, cancellationToken);

    public Task ReapExpiredAsync(CancellationToken cancellationToken) =>
        inner.ReapExpiredAsync(cancellationToken);

    public Task<InboxMessage> GetAsync(string messageId, CancellationToken cancellationToken) =>
        inner.GetAsync(messageId, cancellationToken);
}
