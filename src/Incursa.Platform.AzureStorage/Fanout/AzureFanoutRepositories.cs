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

internal sealed record AzureFanoutPolicyModel
{
    public string FanoutTopic { get; init; } = string.Empty;

    public string WorkKey { get; init; } = string.Empty;

    public int EverySeconds { get; set; }

    public int JitterSeconds { get; set; }

    public DateTimeOffset LastUpdatedAt { get; set; }
}

internal sealed record AzureFanoutCursorModel
{
    public string FanoutTopic { get; init; } = string.Empty;

    public string WorkKey { get; init; } = string.Empty;

    public string ShardKey { get; init; } = string.Empty;

    public DateTimeOffset LastCompletedAt { get; set; }
}

internal sealed class AzureFanoutResources
{
    public AzureFanoutResources(
        AzurePlatformClientFactory clientFactory,
        AzurePlatformOptions options,
        AzurePlatformNameResolver nameResolver,
        AzurePlatformJsonSerializer serializer,
        ILoggerFactory loggerFactory)
    {
        Table = new AzurePlatformTable(
            clientFactory,
            options,
            loggerFactory.CreateLogger<AzureFanoutResources>(),
            nameResolver.GetFanoutTableName());
        Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    public AzurePlatformTable Table { get; }

    public AzurePlatformJsonSerializer Serializer { get; }
}

internal sealed class AzureFanoutPolicyRepository : IFanoutPolicyRepository
{
    private const int DefaultEverySeconds = 300;
    private const int DefaultJitterSeconds = 60;

    private readonly AzureFanoutResources resources;
    private readonly TimeProvider timeProvider;

    public AzureFanoutPolicyRepository(AzureFanoutResources resources, TimeProvider timeProvider)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<(int everySeconds, int jitterSeconds)> GetCadenceAsync(string fanoutTopic, string workKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fanoutTopic);
        ArgumentException.ThrowIfNullOrWhiteSpace(workKey);
        await resources.Table.EnsureReadyAsync(ct).ConfigureAwait(false);

        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Policy(fanoutTopic, workKey),
                cancellationToken: ct)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return (DefaultEverySeconds, DefaultJitterSeconds);
        }

        AzureFanoutPolicyModel model = Deserialize<AzureFanoutPolicyModel>(response.Value!);
        return (model.EverySeconds, model.JitterSeconds);
    }

    public async Task SetCadenceAsync(string fanoutTopic, string workKey, int everySeconds, int jitterSeconds, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fanoutTopic);
        ArgumentException.ThrowIfNullOrWhiteSpace(workKey);
        if (everySeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(everySeconds), everySeconds, "Cadence must be positive.");
        }

        if (jitterSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterSeconds), jitterSeconds, "Jitter must be non-negative.");
        }

        await resources.Table.EnsureReadyAsync(ct).ConfigureAwait(false);

        AzureFanoutPolicyModel model = new()
        {
            FanoutTopic = fanoutTopic,
            WorkKey = workKey,
            EverySeconds = everySeconds,
            JitterSeconds = jitterSeconds,
            LastUpdatedAt = timeProvider.GetUtcNow(),
        };

        await resources.Table.Client.UpsertEntityAsync(
                CreateEntity(AzurePlatformRowKeys.Policy(fanoutTopic, workKey), "FanoutPolicy", model),
                TableUpdateMode.Replace,
                ct)
            .ConfigureAwait(false);
    }

    private TableEntity CreateEntity(string rowKey, string entityType, object model)
    {
        return new TableEntity(AzurePlatformTableConstants.PartitionKey, rowKey)
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

internal sealed class AzureFanoutCursorRepository : IFanoutCursorRepository
{
    private readonly AzureFanoutResources resources;

    public AzureFanoutCursorRepository(AzureFanoutResources resources)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    public async Task<DateTimeOffset?> GetLastAsync(string fanoutTopic, string workKey, string shardKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fanoutTopic);
        ArgumentException.ThrowIfNullOrWhiteSpace(workKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(shardKey);
        await resources.Table.EnsureReadyAsync(ct).ConfigureAwait(false);

        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.Cursor(fanoutTopic, workKey, shardKey),
                cancellationToken: ct)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return null;
        }

        return Deserialize<AzureFanoutCursorModel>(response.Value!).LastCompletedAt;
    }

    public async Task MarkCompletedAsync(string fanoutTopic, string workKey, string shardKey, DateTimeOffset completedAt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fanoutTopic);
        ArgumentException.ThrowIfNullOrWhiteSpace(workKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(shardKey);
        await resources.Table.EnsureReadyAsync(ct).ConfigureAwait(false);

        AzureFanoutCursorModel model = new()
        {
            FanoutTopic = fanoutTopic,
            WorkKey = workKey,
            ShardKey = shardKey,
            LastCompletedAt = completedAt,
        };

        await resources.Table.Client.UpsertEntityAsync(
                CreateEntity(AzurePlatformRowKeys.Cursor(fanoutTopic, workKey, shardKey), "FanoutCursor", model),
                TableUpdateMode.Replace,
                ct)
            .ConfigureAwait(false);
    }

    private TableEntity CreateEntity(string rowKey, string entityType, object model)
    {
        return new TableEntity(AzurePlatformTableConstants.PartitionKey, rowKey)
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
