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
using Incursa.Platform.Outbox;

namespace Incursa.Platform;

internal sealed class AzureOutboxJoinStore : IOutboxJoinStore
{
    private readonly AzureOutboxResources resources;
    private readonly TimeProvider timeProvider;

    public AzureOutboxJoinStore(AzureOutboxResources resources, TimeProvider timeProvider)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<OutboxJoin> CreateJoinAsync(
        long tenantId,
        int expectedSteps,
        string? metadata,
        CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        Guid joinId = Guid.NewGuid();

        AzureOutboxJoinModel model = new()
        {
            JoinId = joinId,
            TenantId = tenantId,
            ExpectedSteps = expectedSteps,
            CompletedSteps = 0,
            FailedSteps = 0,
            Status = 0,
            CreatedUtc = now,
            LastUpdatedUtc = now,
            Metadata = string.IsNullOrWhiteSpace(metadata)
                ? null
                : await resources.PayloadStore.StoreTextAsync("outbox-join", joinId.ToString("N"), metadata, cancellationToken).ConfigureAwait(false),
        };

        await resources.Table.Client.AddEntityAsync(CreateEntity(AzurePlatformRowKeys.Join(joinId), "OutboxJoin", model), cancellationToken).ConfigureAwait(false);
        return await ToOutboxJoinAsync(model, cancellationToken).ConfigureAwait(false);
    }

    public async Task AttachMessageToJoinAsync(
        JoinIdentifier joinId,
        OutboxMessageIdentifier outboxMessageId,
        CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await resources.Table.Client.AddEntityAsync(
                    CreateEntity(
                        AzurePlatformRowKeys.JoinMember(joinId.Value, outboxMessageId.Value),
                        "OutboxJoinMember",
                        new AzureOutboxJoinMemberModel
                        {
                            JoinId = joinId.Value,
                            OutboxMessageId = outboxMessageId.Value,
                            CreatedUtc = timeProvider.GetUtcNow(),
                        }),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
        {
            // Idempotent attach.
        }
    }

    public async Task<OutboxJoin?> GetJoinAsync(JoinIdentifier joinId, CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Join(joinId.Value),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return null;
        }

        AzureOutboxJoinModel model = Deserialize<AzureOutboxJoinModel>(response.Value!);
        return await ToOutboxJoinAsync(model, cancellationToken).ConfigureAwait(false);
    }

    public Task<OutboxJoin> IncrementCompletedAsync(
        JoinIdentifier joinId,
        OutboxMessageIdentifier outboxMessageId,
        CancellationToken cancellationToken) =>
        IncrementAsync(joinId, outboxMessageId, isFailure: false, cancellationToken);

    public Task<OutboxJoin> IncrementFailedAsync(
        JoinIdentifier joinId,
        OutboxMessageIdentifier outboxMessageId,
        CancellationToken cancellationToken) =>
        IncrementAsync(joinId, outboxMessageId, isFailure: true, cancellationToken);

    public async Task UpdateStatusAsync(JoinIdentifier joinId, byte status, CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Join(joinId.Value),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return;
        }

        AzureOutboxJoinModel model = Deserialize<AzureOutboxJoinModel>(response.Value!);
        model.Status = status;
        model.LastUpdatedUtc = timeProvider.GetUtcNow();
        await resources.Table.Client.UpdateEntityAsync(CreateEntity(response.Value!, "OutboxJoin", model), response.Value!.ETag, TableUpdateMode.Replace, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutboxMessageIdentifier>> GetJoinMessagesAsync(
        JoinIdentifier joinId,
        CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        List<OutboxMessageIdentifier> result = [];
        string prefix = $"join-member|{joinId.Value:N}|";
        string upperBound = prefix + "~";

        await foreach (TableEntity entity in resources.Table.Client.QueryAsync<TableEntity>(
                           filter: $"PartitionKey eq '{AzurePlatformTableConstants.PartitionKey}' and RowKey ge '{prefix}' and RowKey le '{upperBound}'",
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            AzureOutboxJoinMemberModel model = Deserialize<AzureOutboxJoinMemberModel>(entity);
            result.Add(OutboxMessageIdentifier.From(model.OutboxMessageId));
        }

        return result;
    }

    private async Task<OutboxJoin> IncrementAsync(
        JoinIdentifier joinId,
        OutboxMessageIdentifier outboxMessageId,
        bool isFailure,
        CancellationToken cancellationToken)
    {
        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        string joinRowKey = AzurePlatformRowKeys.Join(joinId.Value);
        string memberRowKey = AzurePlatformRowKeys.JoinMember(joinId.Value, outboxMessageId.Value);
        NullableResponse<TableEntity> joinResponse = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                joinRowKey,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        NullableResponse<TableEntity> memberResponse = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                memberRowKey,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!joinResponse.HasValue)
        {
            throw new InvalidOperationException($"Join '{joinId}' was not found.");
        }

        if (!memberResponse.HasValue)
        {
            throw new InvalidOperationException($"Join member '{outboxMessageId}' was not found for join '{joinId}'.");
        }

        AzureOutboxJoinModel joinModel = Deserialize<AzureOutboxJoinModel>(joinResponse.Value!);
        AzureOutboxJoinMemberModel memberModel = Deserialize<AzureOutboxJoinMemberModel>(memberResponse.Value!);

        if (memberModel.CompletedAt is not null || memberModel.FailedAt is not null)
        {
            return await ToOutboxJoinAsync(joinModel, cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (isFailure)
        {
            joinModel.FailedSteps += 1;
            joinModel.Status = 2;
            memberModel.FailedAt = now;
        }
        else
        {
            joinModel.CompletedSteps += 1;
            memberModel.CompletedAt = now;
            if (joinModel.CompletedSteps == joinModel.ExpectedSteps && joinModel.FailedSteps == 0)
            {
                joinModel.Status = 1;
            }
        }

        joinModel.LastUpdatedUtc = now;
        await resources.Table.Client.SubmitTransactionAsync(
            [
                new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(joinResponse.Value!, "OutboxJoin", joinModel), joinResponse.Value!.ETag),
                new TableTransactionAction(TableTransactionActionType.UpdateReplace, CreateEntity(memberResponse.Value!, "OutboxJoinMember", memberModel), memberResponse.Value!.ETag),
            ],
            cancellationToken).ConfigureAwait(false);

        return await ToOutboxJoinAsync(joinModel, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OutboxJoin> ToOutboxJoinAsync(AzureOutboxJoinModel model, CancellationToken cancellationToken)
    {
        string? metadata = model.Metadata is null
            ? null
            : await resources.PayloadStore.ReadTextAsync(model.Metadata, cancellationToken).ConfigureAwait(false);

        return new OutboxJoin
        {
            JoinId = JoinIdentifier.From(model.JoinId),
            TenantId = model.TenantId,
            ExpectedSteps = model.ExpectedSteps,
            CompletedSteps = model.CompletedSteps,
            FailedSteps = model.FailedSteps,
            Status = model.Status,
            CreatedUtc = model.CreatedUtc,
            LastUpdatedUtc = model.LastUpdatedUtc,
            Metadata = metadata,
        };
    }

    private TableEntity CreateEntity(string rowKey, string entityType, object model)
    {
        return new TableEntity(AzurePlatformTableConstants.PartitionKey, rowKey)
        {
            [AzurePlatformTableConstants.EntityTypeProperty] = entityType,
            [AzurePlatformTableConstants.DataProperty] = resources.Serializer.SerializeToString(model),
        };
    }

    private TableEntity CreateEntity(TableEntity currentEntity, string entityType, object model)
    {
        return new TableEntity(currentEntity.PartitionKey, currentEntity.RowKey)
        {
            [AzurePlatformTableConstants.EntityTypeProperty] = entityType,
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
}
