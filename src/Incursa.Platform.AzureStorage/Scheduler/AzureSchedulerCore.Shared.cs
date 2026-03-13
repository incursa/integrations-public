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
    private async IAsyncEnumerable<TableEntity> QueryPrefixAsync(
        string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string upperBound = prefix + "~";
        await foreach (TableEntity entity in resources.Table.Client.QueryAsync<TableEntity>(
                           filter: $"PartitionKey eq '{AzurePlatformTableConstants.PartitionKey}' and RowKey ge '{prefix}' and RowKey lt '{upperBound}'",
                           maxPerPage: 100,
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            yield return entity;
        }
    }

    private async Task<DateTimeOffset?> GetFirstDueTimeForPrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        await foreach (TableEntity entity in QueryPrefixAsync(prefix, cancellationToken).ConfigureAwait(false))
        {
            return ParseDueFromRowKey(entity.RowKey);
        }

        return null;
    }

    private async Task CleanupStaleRowAsync(string? expectedRowKey, TableEntity actualEntity, CancellationToken cancellationToken)
    {
        if (string.Equals(expectedRowKey, actualEntity.RowKey, StringComparison.Ordinal))
        {
            return;
        }

        await DeleteEntityBestEffortAsync(actualEntity, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteEntityBestEffortAsync(TableEntity entity, CancellationToken cancellationToken)
    {
        try
        {
            await resources.Table.Client.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, entity.ETag, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception) || AzurePlatformExceptionHelper.IsNotFound(exception))
        {
            logger.LogDebug(exception, "Ignoring best-effort scheduler cleanup race for row {RowKey}.", entity.RowKey);
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

    private async Task SignalIfReadyAsync(DateTimeOffset dueTimeUtc, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (dueTimeUtc <= now.AddSeconds(1))
        {
            await resources.SignalQueue.SendSignalAsync(SignalName, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureSchedulerFenceAsync(ISystemLease lease, CancellationToken cancellationToken)
    {
        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                AzurePlatformRowKeys.SchedulerState(),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return;
        }

        AzureSchedulerStateModel state = Deserialize<AzureSchedulerStateModel>(response.Value!);
        if (lease.FencingToken < state.CurrentFencingToken)
        {
            throw new LostLeaseException(LeaseResourceName, lease.OwnerToken);
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

    private static DateTimeOffset CalculateNextDueTime(string cronSchedule, DateTimeOffset now)
    {
        string[] parts = cronSchedule.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        CronFormat format = parts.Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
        CronExpression expression = CronExpression.Parse(cronSchedule, format);
        DateTime? nextOccurrence = expression.GetNextOccurrence(now.UtcDateTime, TimeZoneInfo.Utc);
        if (!nextOccurrence.HasValue)
        {
            throw new NotSupportedException($"Cron schedule '{cronSchedule}' does not produce a future occurrence.");
        }

        return new DateTimeOffset(nextOccurrence.Value, TimeSpan.Zero);
    }

    private static bool IsOwnedBy(string? currentOwnerToken, OwnerToken expectedOwnerToken) =>
        string.Equals(currentOwnerToken, expectedOwnerToken.Value.ToString("N"), StringComparison.OrdinalIgnoreCase);

    private static TimeSpan GetDispatchBackoff(int attempt)
    {
        int cappedAttempt = Math.Min(8, Math.Max(1, attempt));
        int delaySeconds = (int)Math.Pow(2, cappedAttempt - 1);
        return TimeSpan.FromSeconds(Math.Min(30, delaySeconds));
    }

    private static DateTimeOffset ParseDueFromRowKey(string rowKey)
    {
        string[] segments = rowKey.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !long.TryParse(segments[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long dueMilliseconds))
        {
            throw new InvalidOperationException($"Scheduler row key '{rowKey}' does not contain a parseable due time.");
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(dueMilliseconds);
    }

    private static Guid ParseGuidSuffix(string rowKey)
    {
        int separatorIndex = rowKey.LastIndexOf('|');
        return Guid.ParseExact(rowKey[(separatorIndex + 1)..], "N");
    }

    private static long ToUnixMilliseconds(DateTimeOffset value) => value.ToUniversalTime().ToUnixTimeMilliseconds();
}
