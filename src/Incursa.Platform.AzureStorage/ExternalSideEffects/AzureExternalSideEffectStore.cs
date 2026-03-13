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

internal sealed record AzureExternalSideEffectModel
{
    public Guid Id { get; init; }

    public string OperationName { get; init; } = string.Empty;

    public string IdempotencyKey { get; init; } = string.Empty;

    public ExternalSideEffectStatus Status { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset LastUpdatedAt { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    public DateTimeOffset? LastExternalCheckAt { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public Guid? LockedBy { get; set; }

    public string? CorrelationId { get; init; }

    public Guid? OutboxMessageId { get; init; }

    public string? ExternalReferenceId { get; set; }

    public string? ExternalStatus { get; set; }

    public string? LastError { get; set; }

    public string? PayloadHash { get; init; }
}

internal sealed class AzureExternalSideEffectResources
{
    public AzureExternalSideEffectResources(
        AzurePlatformClientFactory clientFactory,
        AzurePlatformOptions options,
        AzurePlatformNameResolver nameResolver,
        AzurePlatformJsonSerializer serializer,
        ILoggerFactory loggerFactory)
    {
        Table = new AzurePlatformTable(
            clientFactory,
            options,
            loggerFactory.CreateLogger<AzureExternalSideEffectResources>(),
            nameResolver.GetExternalSideEffectTableName());
        Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    public AzurePlatformTable Table { get; }

    public AzurePlatformJsonSerializer Serializer { get; }
}

internal sealed class AzureExternalSideEffectStore : IExternalSideEffectStore
{
    private readonly AzureExternalSideEffectResources resources;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AzureExternalSideEffectStore> logger;
    private readonly OwnerToken ownerToken = OwnerToken.GenerateNew();

    public AzureExternalSideEffectStore(
        AzureExternalSideEffectResources resources,
        TimeProvider timeProvider,
        ILogger<AzureExternalSideEffectStore> logger)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ExternalSideEffectRecord?> GetAsync(ExternalSideEffectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Effect(key.OperationName, key.IdempotencyKey),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.HasValue ? ToRecord(Deserialize(response.Value!)) : null;
    }

    public async Task<ExternalSideEffectRecord> GetOrCreateAsync(ExternalSideEffectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        ExternalSideEffectRecord? existing = await GetAsync(request.Key, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        AzureExternalSideEffectModel model = new()
        {
            Id = Guid.NewGuid(),
            OperationName = request.Key.OperationName,
            IdempotencyKey = request.Key.IdempotencyKey,
            Status = ExternalSideEffectStatus.Pending,
            CreatedAt = now,
            LastUpdatedAt = now,
            CorrelationId = request.CorrelationId,
            OutboxMessageId = request.OutboxMessageId,
            PayloadHash = request.PayloadHash,
        };

        try
        {
            await resources.Table.Client.AddEntityAsync(
                    CreateEntity(AzurePlatformRowKeys.Effect(request.Key.OperationName, request.Key.IdempotencyKey), model),
                    cancellationToken)
                .ConfigureAwait(false);
            return ToRecord(model);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
        {
            logger.LogDebug(exception, "External side-effect record already exists for {OperationName}/{IdempotencyKey}.", request.Key.OperationName, request.Key.IdempotencyKey);
            return await GetAsync(request.Key, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("External side-effect record could not be created or retrieved.");
        }
    }

    public async Task<ExternalSideEffectAttempt> TryBeginAttemptAsync(
        ExternalSideEffectKey key,
        TimeSpan lockDuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (lockDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lockDuration), lockDuration, "Lock duration must be positive.");
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            ExternalSideEffectRecord record = await GetOrCreateAsync(new ExternalSideEffectRequest("default", key), cancellationToken).ConfigureAwait(false);
            NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.Effect(key.OperationName, key.IdempotencyKey),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.HasValue)
            {
                continue;
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            AzureExternalSideEffectModel current = Deserialize(response.Value!);
            if (current.Status is ExternalSideEffectStatus.Succeeded or ExternalSideEffectStatus.Failed)
            {
                return new ExternalSideEffectAttempt(ExternalSideEffectAttemptDecision.AlreadyCompleted, ToRecord(current));
            }

            if (current.LockedUntil is DateTimeOffset lockedUntil &&
                lockedUntil > now &&
                current.LockedBy.HasValue &&
                current.LockedBy.Value != ownerToken.Value)
            {
                return new ExternalSideEffectAttempt(
                    ExternalSideEffectAttemptDecision.Locked,
                    ToRecord(current),
                    $"Locked until {lockedUntil:O}.");
            }

            current.Status = ExternalSideEffectStatus.InFlight;
            current.AttemptCount += 1;
            current.LastAttemptAt = now;
            current.LastUpdatedAt = now;
            current.LockedUntil = now.Add(lockDuration);
            current.LockedBy = ownerToken.Value;
            current.LastError = null;

            try
            {
                await resources.Table.Client.UpdateEntityAsync(
                        CreateEntity(response.Value!, current),
                        response.Value!.ETag,
                        TableUpdateMode.Replace,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new ExternalSideEffectAttempt(ExternalSideEffectAttemptDecision.Ready, ToRecord(current));
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "External side-effect attempt begin for {OperationName}/{IdempotencyKey} lost an optimistic concurrency race.", key.OperationName, key.IdempotencyKey);
            }
        }

        throw new InvalidOperationException($"External side effect '{key.OperationName}/{key.IdempotencyKey}' could not begin an attempt after repeated optimistic concurrency retries.");
    }

    public Task RecordExternalCheckAsync(
        ExternalSideEffectKey key,
        ExternalSideEffectCheckResult result,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken) =>
        MutateAsync(
            key,
            model =>
            {
                model.LastExternalCheckAt = checkedAt;
                model.ExternalReferenceId = result.ExternalReferenceId ?? model.ExternalReferenceId;
                model.ExternalStatus = result.ExternalStatus ?? model.ExternalStatus;
                model.LastUpdatedAt = checkedAt;
            },
            cancellationToken);

    public Task MarkSucceededAsync(
        ExternalSideEffectKey key,
        ExternalSideEffectExecutionResult result,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken) =>
        MutateAsync(
            key,
            model =>
            {
                model.Status = ExternalSideEffectStatus.Succeeded;
                model.ExternalReferenceId = result.ExternalReferenceId;
                model.ExternalStatus = result.ExternalStatus;
                model.LastError = null;
                model.LockedUntil = null;
                model.LockedBy = null;
                model.LastUpdatedAt = completedAt;
            },
            cancellationToken);

    public Task MarkFailedAsync(
        ExternalSideEffectKey key,
        string errorMessage,
        bool isPermanent,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken) =>
        MutateAsync(
            key,
            model =>
            {
                model.Status = isPermanent ? ExternalSideEffectStatus.Failed : ExternalSideEffectStatus.Pending;
                model.LastError = errorMessage;
                model.LockedUntil = null;
                model.LockedBy = null;
                model.LastUpdatedAt = failedAt;
            },
            cancellationToken);

    private async Task MutateAsync(
        ExternalSideEffectKey key,
        Action<AzureExternalSideEffectModel> mutator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(mutator);
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    AzurePlatformRowKeys.Effect(key.OperationName, key.IdempotencyKey),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.HasValue)
            {
                return;
            }

            AzureExternalSideEffectModel current = Deserialize(response.Value!);
            mutator(current);

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
                logger.LogDebug(exception, "External side-effect mutation for {OperationName}/{IdempotencyKey} lost an optimistic concurrency race.", key.OperationName, key.IdempotencyKey);
            }
        }

        throw new InvalidOperationException($"External side effect '{key.OperationName}/{key.IdempotencyKey}' could not be updated after repeated optimistic concurrency retries.");
    }

    private ExternalSideEffectRecord ToRecord(AzureExternalSideEffectModel model)
    {
        return new ExternalSideEffectRecord
        {
            Id = model.Id,
            OperationName = model.OperationName,
            IdempotencyKey = model.IdempotencyKey,
            Status = model.Status,
            AttemptCount = model.AttemptCount,
            CreatedAt = model.CreatedAt,
            LastUpdatedAt = model.LastUpdatedAt,
            LastAttemptAt = model.LastAttemptAt,
            LastExternalCheckAt = model.LastExternalCheckAt,
            LockedUntil = model.LockedUntil,
            LockedBy = model.LockedBy,
            CorrelationId = model.CorrelationId,
            OutboxMessageId = model.OutboxMessageId,
            ExternalReferenceId = model.ExternalReferenceId,
            ExternalStatus = model.ExternalStatus,
            LastError = model.LastError,
            PayloadHash = model.PayloadHash,
        };
    }

    private TableEntity CreateEntity(string rowKey, AzureExternalSideEffectModel model)
    {
        return new TableEntity(AzurePlatformTableConstants.PartitionKey, rowKey)
        {
            [AzurePlatformTableConstants.EntityTypeProperty] = "ExternalSideEffect",
            [AzurePlatformTableConstants.DataProperty] = resources.Serializer.SerializeToString(model),
        };
    }

    private TableEntity CreateEntity(TableEntity currentEntity, AzureExternalSideEffectModel model)
    {
        return new TableEntity(currentEntity.PartitionKey, currentEntity.RowKey)
        {
            [AzurePlatformTableConstants.EntityTypeProperty] = currentEntity.GetString(AzurePlatformTableConstants.EntityTypeProperty),
            [AzurePlatformTableConstants.DataProperty] = resources.Serializer.SerializeToString(model),
        };
    }

    private AzureExternalSideEffectModel Deserialize(TableEntity entity)
    {
        string json = entity.GetString(AzurePlatformTableConstants.DataProperty)
            ?? throw new InvalidOperationException($"Azure Table entity '{entity.RowKey}' does not contain serialized data.");
        return resources.Serializer.Deserialize<AzureExternalSideEffectModel>(json)
            ?? throw new InvalidOperationException($"Azure Table entity '{entity.RowKey}' could not be deserialized as {nameof(AzureExternalSideEffectModel)}.");
    }
}
