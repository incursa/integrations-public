// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform.Outbox;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>SQLite durable outbox work-queue store.</summary>
internal sealed class SqliteOutboxStore : IOutboxStore
{
    private readonly SqliteConnectionFactory factory;
    private readonly SqliteRetryPolicy retry;
    private readonly TimeProvider timeProvider;
    private readonly OwnerToken ownerToken = OwnerToken.GenerateNew();
    private int schemaEnsured;

    public SqliteOutboxStore(SqliteConnectionFactory factory, SqliteRetryPolicy retry, TimeProvider? timeProvider = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.retry = retry ?? throw new ArgumentNullException(nameof(retry));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var ids = await ClaimAsync(ownerToken, 300, limit, cancellationToken).ConfigureAwait(false);
        return await ReadMessagesAsync(ids, cancellationToken).ConfigureAwait(false);
    }

    public Task MarkDispatchedAsync(OutboxWorkItemIdentifier id, CancellationToken cancellationToken) => AckAsync(ownerToken, [id], cancellationToken);

    public Task RescheduleAsync(OutboxWorkItemIdentifier id, TimeSpan delay, string lastError, CancellationToken cancellationToken)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be non-negative.");
        }
        return AbandonAsync(ownerToken, [id], lastError, delay, cancellationToken);
    }

    public Task FailAsync(OutboxWorkItemIdentifier id, string lastError, CancellationToken cancellationToken) => FailAsync(ownerToken, [id], lastError, cancellationToken);

    internal async Task<IReadOnlyList<OutboxWorkItemIdentifier>> ClaimAsync(OwnerToken token, int leaseSeconds, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(leaseSeconds);
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var nowText = SqliteOutboxDatabase.Timestamp(now)!;
        var untilText = SqliteOutboxDatabase.Timestamp(now.AddSeconds(leaseSeconds))!;

        return await retry.ExecuteAsync(async ct =>
        {
            using var connection = factory.OpenConnection();
            await SqliteOutboxDatabase.BeginImmediateAsync(connection, ct).ConfigureAwait(false);
            try
            {
                var ids = new List<OutboxWorkItemIdentifier>();
                using (var select = SqliteOutboxDatabase.Command(connection, $"""
                    SELECT "Id" FROM {SqliteOutboxSchema.OutboxTable}
                    WHERE ("Status" = 0 OR ("Status" = 1 AND "LockedUntil" IS NOT NULL AND "LockedUntil" <= $now))
                      AND ("DueTimeUtc" IS NULL OR "DueTimeUtc" <= $now)
                    ORDER BY "CreatedAt", "Id" LIMIT $limit;
                    """))
                {
                    SqliteOutboxDatabase.AddParameter(select, "$now", nowText);
                    SqliteOutboxDatabase.AddParameter(select, "$limit", batchSize);
                    using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false)) ids.Add(SqliteOutboxDatabase.WorkItem(reader.GetValue(0)));
                }

                var claimed = new List<OutboxWorkItemIdentifier>(ids.Count);
                foreach (var id in ids)
                {
                    using var update = SqliteOutboxDatabase.Command(connection, $"UPDATE {SqliteOutboxSchema.OutboxTable} SET \"Status\" = 1, \"OwnerToken\" = $owner, \"LockedUntil\" = $until WHERE \"Id\" = $id AND (\"Status\" = 0 OR (\"Status\" = 1 AND \"LockedUntil\" IS NOT NULL AND \"LockedUntil\" <= $now));");
                    SqliteOutboxDatabase.AddParameter(update, "$owner", SqliteOutboxDatabase.Owner(token));
                    SqliteOutboxDatabase.AddParameter(update, "$until", untilText);
                    SqliteOutboxDatabase.AddParameter(update, "$now", nowText);
                    SqliteOutboxDatabase.AddParameter(update, "$id", SqliteOutboxDatabase.GuidValue(id.Value));
                    if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0) claimed.Add(id);
                }

                await SqliteOutboxDatabase.CommitAsync(connection, ct).ConfigureAwait(false);
                return (IReadOnlyList<OutboxWorkItemIdentifier>)claimed;
            }
            catch
            {
                try { await SqliteOutboxDatabase.RollbackAsync(connection).ConfigureAwait(false); }
                catch (SqliteException) { }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task AckAsync(OwnerToken token, IEnumerable<OutboxWorkItemIdentifier> ids, CancellationToken cancellationToken)
    {
        await TransitionAsync(token, ids, "ack", null, null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task AbandonAsync(OwnerToken token, IEnumerable<OutboxWorkItemIdentifier> ids, string? error, TimeSpan? delay, CancellationToken cancellationToken)
    {
        if (delay.HasValue && delay.Value < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
        await TransitionAsync(token, ids, "abandon", error, delay ?? TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
    }

    internal async Task FailAsync(OwnerToken token, IEnumerable<OutboxWorkItemIdentifier> ids, string? error, CancellationToken cancellationToken)
    {
        await TransitionAsync(token, ids, "fail", error, null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task ReapExpiredAsync(CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        var now = SqliteOutboxDatabase.Timestamp(timeProvider.GetUtcNow())!;
        await retry.ExecuteAsync(async ct =>
        {
            using var connection = factory.OpenConnection();
            using var command = SqliteOutboxDatabase.Command(connection, $"UPDATE {SqliteOutboxSchema.OutboxTable} SET \"Status\" = 0, \"OwnerToken\" = NULL, \"LockedUntil\" = NULL, \"RetryCount\" = \"RetryCount\" + 1 WHERE \"Status\" = 1 AND \"LockedUntil\" IS NOT NULL AND \"LockedUntil\" <= $now;");
            SqliteOutboxDatabase.AddParameter(command, "$now", now);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal Task<IReadOnlyList<OutboxMessage>> ReadMessagesAsync(IEnumerable<OutboxWorkItemIdentifier> ids, CancellationToken cancellationToken)
    {
        var list = ids.ToArray();
        if (list.Length == 0) return Task.FromResult<IReadOnlyList<OutboxMessage>>([]);
        return ReadMessagesCoreAsync(list, cancellationToken);
    }

    private async Task<IReadOnlyList<OutboxMessage>> ReadMessagesCoreAsync(IReadOnlyList<OutboxWorkItemIdentifier> ids, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return await retry.ExecuteAsync(async ct =>
        {
            using var connection = factory.OpenConnection();
            var result = new List<OutboxMessage>();
            foreach (var id in ids)
            {
                using var command = SqliteOutboxDatabase.Command(connection, $"SELECT \"Id\", \"Topic\", \"Payload\", \"CorrelationId\", \"MessageId\", \"CreatedAt\", \"IsProcessed\", \"ProcessedAt\", \"ProcessedBy\", \"RetryCount\", \"LastError\", \"DueTimeUtc\" FROM {SqliteOutboxSchema.OutboxTable} WHERE \"Id\" = $id;");
                SqliteOutboxDatabase.AddParameter(command, "$id", SqliteOutboxDatabase.GuidValue(id.Value));
                using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(ReadMessage(reader));
            }

            return (IReadOnlyList<OutboxMessage>)result;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task TransitionAsync(OwnerToken token, IEnumerable<OutboxWorkItemIdentifier> ids, string action, string? error, TimeSpan? delay, CancellationToken cancellationToken)
    {
        var idList = ids.Distinct().ToArray();
        if (idList.Length == 0) return;
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        await retry.ExecuteAsync(async ct =>
        {
            using var connection = factory.OpenConnection();
            await SqliteOutboxDatabase.BeginImmediateAsync(connection, ct).ConfigureAwait(false);
            try
            {
                foreach (var id in idList)
                {
                    var now = timeProvider.GetUtcNow();
                    var nowText = SqliteOutboxDatabase.Timestamp(now)!;
                    string sql;
                    if (string.Equals(action, "ack", StringComparison.Ordinal))
                    {
                        sql = $"UPDATE {SqliteOutboxSchema.OutboxTable} SET \"Status\" = 2, \"OwnerToken\" = NULL, \"LockedUntil\" = NULL, \"IsProcessed\" = 1, \"ProcessedAt\" = $now WHERE \"Id\" = $id AND \"Status\" = 1 AND \"OwnerToken\" = $owner;";
                    }
                    else if (string.Equals(action, "fail", StringComparison.Ordinal))
                    {
                        sql = $"UPDATE {SqliteOutboxSchema.OutboxTable} SET \"Status\" = 3, \"OwnerToken\" = NULL, \"LockedUntil\" = NULL, \"IsProcessed\" = 0, \"LastError\" = $error, \"ProcessedBy\" = $processedBy WHERE \"Id\" = $id AND \"Status\" = 1 AND \"OwnerToken\" = $owner;";
                    }
                    else
                    {
                        sql = $"UPDATE {SqliteOutboxSchema.OutboxTable} SET \"Status\" = 0, \"OwnerToken\" = NULL, \"LockedUntil\" = NULL, \"RetryCount\" = \"RetryCount\" + 1, \"LastError\" = COALESCE($error, \"LastError\"), \"DueTimeUtc\" = $due WHERE \"Id\" = $id AND \"Status\" = 1 AND \"OwnerToken\" = $owner;";
                    }

                    using var command = SqliteOutboxDatabase.Command(connection, sql);
                    SqliteOutboxDatabase.AddParameter(command, "$now", nowText);
                    SqliteOutboxDatabase.AddParameter(command, "$error", error);
                    SqliteOutboxDatabase.AddParameter(command, "$processedBy", Environment.MachineName + ":FAILED");
                    SqliteOutboxDatabase.AddParameter(command, "$due", SqliteOutboxDatabase.Timestamp(now.Add(delay ?? TimeSpan.Zero))!);
                    SqliteOutboxDatabase.AddParameter(command, "$id", SqliteOutboxDatabase.GuidValue(id.Value));
                    SqliteOutboxDatabase.AddParameter(command, "$owner", SqliteOutboxDatabase.Owner(token));
                    if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0 && (string.Equals(action, "ack", StringComparison.Ordinal) || string.Equals(action, "fail", StringComparison.Ordinal)))
                    {
                        await ApplyJoinAsync(connection, id, completed: string.Equals(action, "ack", StringComparison.Ordinal), nowText, ct).ConfigureAwait(false);
                    }
                }

                await SqliteOutboxDatabase.CommitAsync(connection, ct).ConfigureAwait(false);
            }
            catch
            {
                try { await SqliteOutboxDatabase.RollbackAsync(connection).ConfigureAwait(false); }
                catch (SqliteException) { }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyJoinAsync(SqliteConnection connection, OutboxWorkItemIdentifier id, bool completed, string now, CancellationToken cancellationToken)
    {
        var messageId = SqliteOutboxDatabase.GuidValue(await ReadMessageIdAsync(connection, id, cancellationToken).ConfigureAwait(false));
        var memberColumn = completed ? "CompletedAt" : "FailedAt";
        var counterColumn = completed ? "CompletedSteps" : "FailedSteps";
        var joinIds = new List<string>();
        using (var select = SqliteOutboxDatabase.Command(connection, $"SELECT \"JoinId\" FROM {SqliteOutboxSchema.JoinMemberTable} WHERE \"OutboxMessageId\" = $message AND \"CompletedAt\" IS NULL AND \"FailedAt\" IS NULL;"))
        {
            SqliteOutboxDatabase.AddParameter(select, "$message", messageId);
            using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) joinIds.Add(reader.GetString(0));
        }

        foreach (var joinId in joinIds)
        {
            using var update = SqliteOutboxDatabase.Command(connection, $"UPDATE {SqliteOutboxSchema.JoinMemberTable} SET \"{memberColumn}\" = $now WHERE \"JoinId\" = $join AND \"OutboxMessageId\" = $message AND \"CompletedAt\" IS NULL AND \"FailedAt\" IS NULL;");
            SqliteOutboxDatabase.AddParameter(update, "$now", now);
            SqliteOutboxDatabase.AddParameter(update, "$join", joinId);
            SqliteOutboxDatabase.AddParameter(update, "$message", messageId);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0) continue;

            using var updateJoin = SqliteOutboxDatabase.Command(connection, $"UPDATE {SqliteOutboxSchema.JoinTable} SET \"{counterColumn}\" = \"{counterColumn}\" + 1, \"LastUpdatedUtc\" = $now WHERE \"JoinId\" = $join AND (\"CompletedSteps\" + \"FailedSteps\") < \"ExpectedSteps\";");
            SqliteOutboxDatabase.AddParameter(updateJoin, "$now", now);
            SqliteOutboxDatabase.AddParameter(updateJoin, "$join", joinId);
            await updateJoin.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Guid> ReadMessageIdAsync(SqliteConnection connection, OutboxWorkItemIdentifier id, CancellationToken cancellationToken)
    {
        using var command = SqliteOutboxDatabase.Command(connection, $"SELECT \"MessageId\" FROM {SqliteOutboxSchema.OutboxTable} WHERE \"Id\" = $id;");
        SqliteOutboxDatabase.AddParameter(command, "$id", SqliteOutboxDatabase.GuidValue(id.Value));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Outbox item {id} not found.");
        return Guid.Parse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
    }

    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref schemaEnsured) != 0) return;
        await SqliteOutboxSchema.EnsureAsync(factory, retry, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref schemaEnsured, 1);
    }

    private static OutboxMessage ReadMessage(SqliteDataReader reader) => new()
    {
        Id = SqliteOutboxDatabase.WorkItem(reader.GetValue(0)),
        Topic = reader.GetString(1),
        Payload = reader.GetString(2),
        CorrelationId = reader.IsDBNull(3) ? null : reader.GetString(3),
        MessageId = SqliteOutboxDatabase.Message(reader.GetValue(4)),
        CreatedAt = SqliteOutboxDatabase.TimestampValue(reader.GetValue(5)),
        IsProcessed = reader.GetInt64(6) != 0,
        ProcessedAt = SqliteOutboxDatabase.NullableTimestampValue(reader.IsDBNull(7) ? null : reader.GetValue(7)),
        ProcessedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
        RetryCount = reader.GetInt32(9),
        LastError = reader.IsDBNull(10) ? null : reader.GetString(10),
        DueTimeUtc = SqliteOutboxDatabase.NullableTimestampValue(reader.IsDBNull(11) ? null : reader.GetValue(11)),
    };
}
