// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>SQLite work-queue implementation for inbox messages.</summary>
internal sealed class SqliteInboxWorkStore : IInboxWorkStore
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;
    private readonly TimeProvider timeProvider;
    private readonly string table;
    private readonly int maxAttempts;

    /// <summary>Initializes an SQLite inbox work store.</summary>
    public SqliteInboxWorkStore(
        SqliteConnectionFactory connectionFactory,
        SqliteRetryPolicy retryPolicy,
        SqliteInboxOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.connectionFactory = connectionFactory;
        this.retryPolicy = retryPolicy;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        table = SqliteInboxSql.Table(options);
        maxAttempts = options.MaxAttempts;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ClaimAsync(
        OwnerToken ownerToken,
        int leaseSeconds,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (leaseSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        return retryPolicy.ExecuteAsync(
            ct => ClaimCoreAsync(ownerToken, leaseSeconds, batchSize, ct),
            cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ClaimCoreAsync(
        OwnerToken ownerToken,
        int leaseSeconds,
        int batchSize,
        CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.OpenConnection();
        await SqliteInboxSql.BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            var nowText = SqliteInboxSql.Timestamp(now);
            var lockText = SqliteInboxSql.Timestamp(now.AddSeconds(leaseSeconds));
            var ownerText = SqliteInboxSql.Owner(ownerToken);
            var ids = new List<string>(batchSize);

            using (var select = connection.CreateCommand())
            {
                select.CommandText = $"""
                    SELECT "MessageId"
                    FROM {table}
                    WHERE "Status" IN ('Seen', 'Processing')
                      AND "Attempts" <= $maxAttempts
                      AND ("LockedUntil" IS NULL OR "LockedUntil" <= $now)
                      AND ("DueTimeUtc" IS NULL OR "DueTimeUtc" <= $now)
                    ORDER BY "LastSeenUtc", "MessageId"
                    LIMIT $batchSize;
                    """;
                select.Parameters.Add(SqliteInboxSql.Parameter("$now", nowText));
                select.Parameters.Add(SqliteInboxSql.Parameter("$maxAttempts", maxAttempts));
                select.Parameters.Add(SqliteInboxSql.Parameter("$batchSize", batchSize));
                using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    ids.Add(reader.GetString(0));
                }
            }

            var claimed = new List<string>(ids.Count);
            foreach (var messageId in ids)
            {
                using var update = connection.CreateCommand();
                update.CommandText = $"""
                    UPDATE {table}
                    SET "Status" = 'Processing', "OwnerToken" = $owner, "LockedUntil" = $lockedUntil, "LastSeenUtc" = $now
                    WHERE "MessageId" = $messageId
                      AND "Status" IN ('Seen', 'Processing')
                      AND ("LockedUntil" IS NULL OR "LockedUntil" <= $now);
                    """;
                update.Parameters.Add(SqliteInboxSql.Parameter("$owner", ownerText));
                update.Parameters.Add(SqliteInboxSql.Parameter("$lockedUntil", lockText));
                update.Parameters.Add(SqliteInboxSql.Parameter("$now", nowText));
                update.Parameters.Add(SqliteInboxSql.Parameter("$messageId", messageId));
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                {
                    claimed.Add(messageId);
                }
            }

            await SqliteInboxSql.CommitAsync(connection, cancellationToken).ConfigureAwait(false);
            return claimed;
        }
        catch
        {
            await SqliteInboxSql.RollbackAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public Task AckAsync(OwnerToken ownerToken, IEnumerable<string> messageIds, CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(messageIds);
        return ids.Count == 0
            ? Task.CompletedTask
            : UpdateOwnedAsync(
                ownerToken,
                ids,
                "Status = 'Done', OwnerToken = NULL, LockedUntil = NULL, ProcessedUtc = $now, LastSeenUtc = $now",
                cancellationToken);
    }

    /// <inheritdoc />
    public Task AbandonAsync(
        OwnerToken ownerToken,
        IEnumerable<string> messageIds,
        string? lastError = null,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default)
    {
        var ids = NormalizeIds(messageIds);
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be non-negative.");
        }

        return ids.Count == 0
            ? Task.CompletedTask
            : UpdateOwnedAsync(
                ownerToken,
                ids,
                "Status = 'Seen', OwnerToken = NULL, LockedUntil = NULL, Attempts = Attempts + 1, LastSeenUtc = $now, LastError = COALESCE($lastError, LastError), DueTimeUtc = COALESCE($dueTimeUtc, DueTimeUtc)",
                cancellationToken,
                lastError,
                delay);
    }

    /// <inheritdoc />
    public Task FailAsync(OwnerToken ownerToken, IEnumerable<string> messageIds, string errorMessage, CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(messageIds);
        return ids.Count == 0
            ? Task.CompletedTask
            : UpdateOwnedAsync(
                ownerToken,
                ids,
                "Status = 'Dead', OwnerToken = NULL, LockedUntil = NULL, LastSeenUtc = $now, LastError = COALESCE($lastError, LastError)",
                cancellationToken,
                errorMessage,
                null);
    }

    /// <inheritdoc />
    public Task ReviveAsync(
        IEnumerable<string> messageIds,
        string? reason = null,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default)
    {
        var ids = NormalizeIds(messageIds);
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be non-negative.");
        }

        return ids.Count == 0
            ? Task.CompletedTask
            : UpdateAnyAsync(
                ids,
                "Status = 'Seen', OwnerToken = NULL, LockedUntil = NULL, LastSeenUtc = $now, LastError = $lastError, DueTimeUtc = $dueTimeUtc",
                cancellationToken,
                reason,
                delay);
    }

    /// <inheritdoc />
    public Task ReapExpiredAsync(CancellationToken cancellationToken)
    {
        return retryPolicy.ExecuteAsync(
            async ct =>
            {
                using var connection = connectionFactory.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    UPDATE {table}
                    SET "Status" = CASE WHEN "Attempts" >= $maxAttempts THEN 'Dead' ELSE 'Seen' END,
                        "OwnerToken" = NULL,
                        "LockedUntil" = NULL,
                        "Attempts" = "Attempts" + 1,
                        "LastSeenUtc" = $now
                    WHERE "Status" = 'Processing' AND "LockedUntil" IS NOT NULL AND "LockedUntil" <= $now;
                    """;
                command.Parameters.Add(SqliteInboxSql.Parameter("$now", SqliteInboxSql.Timestamp(timeProvider.GetUtcNow())));
                command.Parameters.Add(SqliteInboxSql.Parameter("$maxAttempts", maxAttempts));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>Deletes completed messages older than the supplied retention period.</summary>
    internal Task<int> CleanupAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
    {
        if (retentionPeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionPeriod));
        }

        return retryPolicy.ExecuteAsync(
            async ct =>
            {
                using var connection = connectionFactory.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    DELETE FROM {table}
                    WHERE "Status" = 'Done'
                      AND "ProcessedUtc" IS NOT NULL
                      AND "ProcessedUtc" < $cutoff;
                    """;
                command.Parameters.Add(SqliteInboxSql.Parameter(
                    "$cutoff",
                    SqliteInboxSql.Timestamp(timeProvider.GetUtcNow().Subtract(retentionPeriod))));
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<InboxMessage> GetAsync(string messageId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            throw new ArgumentException("MessageId cannot be null or empty.", nameof(messageId));
        }

        return retryPolicy.ExecuteAsync(ct => GetCoreAsync(messageId, ct), cancellationToken);
    }

    private async Task<InboxMessage> GetCoreAsync(string messageId, CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT "MessageId", "Source", "Topic", "Payload", "Hash", "Attempts", "FirstSeenUtc", "LastSeenUtc", "DueTimeUtc", "LastError"
            FROM {table}
            WHERE "MessageId" = $messageId;
            """;
        command.Parameters.Add(SqliteInboxSql.Parameter("$messageId", messageId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Inbox message '{messageId}' not found.");
        }

        return new InboxMessage
        {
            MessageId = reader.GetString(0),
            Source = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            Topic = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            Payload = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Hash = reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4),
            Attempt = reader.GetInt32(5),
            FirstSeenUtc = SqliteInboxSql.ReadTimestamp(reader.GetValue(6)),
            LastSeenUtc = SqliteInboxSql.ReadTimestamp(reader.GetValue(7)),
            DueTimeUtc = SqliteInboxSql.ReadNullableTimestamp(reader.IsDBNull(8) ? null : reader.GetValue(8)),
            LastError = reader.IsDBNull(9) ? null : reader.GetString(9),
        };
    }

    private Task UpdateOwnedAsync(
        OwnerToken ownerToken,
        IReadOnlyList<string> ids,
        string assignments,
        CancellationToken cancellationToken,
        string? lastError = null,
        TimeSpan? delay = null)
    {
        return retryPolicy.ExecuteAsync(
            ct => UpdateCoreAsync(ownerToken, ids, assignments, true, lastError, delay, ct),
            cancellationToken);
    }

    private Task UpdateAnyAsync(
        IReadOnlyList<string> ids,
        string assignments,
        CancellationToken cancellationToken,
        string? lastError,
        TimeSpan? delay)
    {
        return retryPolicy.ExecuteAsync(
            ct => UpdateCoreAsync(default, ids, assignments, false, lastError, delay, ct),
            cancellationToken);
    }

    private async Task UpdateCoreAsync(
        OwnerToken ownerToken,
        IReadOnlyList<string> ids,
        string assignments,
        bool requireOwner,
        string? lastError,
        TimeSpan? delay,
        CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.OpenConnection();
        await SqliteInboxSql.BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = connection.CreateCommand();
            var names = SqliteInboxSql.BuildIdList(ids, command);
            var ownerPredicate = requireOwner ? " AND \"OwnerToken\" = $owner AND \"Status\" = 'Processing'" : " AND \"Status\" = 'Dead'";
            command.CommandText = $"UPDATE {table} SET {assignments} WHERE \"MessageId\" IN ({names}){ownerPredicate};";
            command.Parameters.Add(SqliteInboxSql.Parameter("$now", SqliteInboxSql.Timestamp(timeProvider.GetUtcNow())));
            command.Parameters.Add(SqliteInboxSql.Parameter("$lastError", string.IsNullOrWhiteSpace(lastError) ? null : lastError.Trim()));
            command.Parameters.Add(SqliteInboxSql.Parameter("$dueTimeUtc", delay.HasValue ? SqliteInboxSql.Timestamp(timeProvider.GetUtcNow().Add(delay.Value)) : null));
            if (requireOwner)
            {
                command.Parameters.Add(SqliteInboxSql.Parameter("$owner", SqliteInboxSql.Owner(ownerToken)));
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await SqliteInboxSql.CommitAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await SqliteInboxSql.RollbackAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    private static List<string> NormalizeIds(IEnumerable<string> messageIds)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        var ids = messageIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        return ids;
    }
}
