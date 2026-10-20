// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>SQLite implementation of the provider-neutral inbox contract.</summary>
internal sealed class SqliteInboxService : IInbox
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;
    private readonly SqliteMigrationRunner? migrationRunner;
    private readonly TimeProvider timeProvider;
    private readonly string tableName;
    private readonly string table;

    /// <summary>Initializes an SQLite inbox service.</summary>
    public SqliteInboxService(
        SqliteConnectionFactory connectionFactory,
        SqliteRetryPolicy retryPolicy,
        SqliteInboxOptions options,
        TimeProvider? timeProvider = null,
        SqliteMigrationRunner? migrationRunner = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.connectionFactory = connectionFactory;
        this.retryPolicy = retryPolicy;
        this.migrationRunner = migrationRunner;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        tableName = options.TableName;
        table = SqliteInboxSql.Table(options);
    }

    /// <inheritdoc />
    public Task<bool> AlreadyProcessedAsync(string messageId, string source, CancellationToken cancellationToken) =>
        AlreadyProcessedAsync(messageId, source, null, cancellationToken);

    /// <inheritdoc />
    public Task<bool> AlreadyProcessedAsync(
        string messageId,
        string source,
        byte[]? hash,
        CancellationToken cancellationToken)
    {
        ValidateMessage(messageId, source);
        return retryPolicy.ExecuteAsync(
            ct => AlreadyProcessedCoreAsync(messageId, source, hash, ct),
            cancellationToken);
    }

    private async Task<bool> AlreadyProcessedCoreAsync(
        string messageId,
        string source,
        byte[]? hash,
        CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.OpenConnection();
        await SqliteInboxSql.BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
        try
        {
            var now = SqliteInboxSql.Timestamp(timeProvider.GetUtcNow());
            using var exists = connection.CreateCommand();
            exists.CommandText = $"SELECT \"ProcessedUtc\" FROM {table} WHERE \"MessageId\" = $messageId;";
            exists.Parameters.Add(SqliteInboxSql.Parameter("$messageId", messageId));
            var processedValue = await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var existsAlready = processedValue is not null && processedValue is not DBNull;

            using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO {table} ("MessageId", "Source", "Hash", "FirstSeenUtc", "LastSeenUtc", "Attempts", "Status")
                VALUES ($messageId, $source, $hash, $now, $now, 1, 'Seen')
                ON CONFLICT ("MessageId") DO UPDATE SET
                    "LastSeenUtc" = $now,
                    "Attempts" = "Attempts" + 1;
                """;
            command.Parameters.Add(SqliteInboxSql.Parameter("$messageId", messageId));
            command.Parameters.Add(SqliteInboxSql.Parameter("$source", source));
            command.Parameters.Add(SqliteInboxSql.Parameter("$hash", hash));
            command.Parameters.Add(SqliteInboxSql.Parameter("$now", now));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await SqliteInboxSql.CommitAsync(connection, cancellationToken).ConfigureAwait(false);
            return existsAlready;
        }
        catch
        {
            await SqliteInboxSql.RollbackAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public Task MarkProcessedAsync(string messageId, CancellationToken cancellationToken)
    {
        ValidateMessageId(messageId);
        return ExecuteMessageUpdateAsync(
            messageId,
            "Status = 'Done', OwnerToken = NULL, LockedUntil = NULL, ProcessedUtc = $now, LastSeenUtc = $now",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task MarkProcessingAsync(string messageId, CancellationToken cancellationToken)
    {
        ValidateMessageId(messageId);
        return ExecuteMessageUpdateAsync(messageId, "Status = 'Processing', LastSeenUtc = $now", cancellationToken);
    }

    /// <inheritdoc />
    public Task MarkDeadAsync(string messageId, CancellationToken cancellationToken)
    {
        ValidateMessageId(messageId);
        return ExecuteMessageUpdateAsync(
            messageId,
            "Status = 'Dead', OwnerToken = NULL, LockedUntil = NULL, LastSeenUtc = $now",
            cancellationToken);
    }

    private Task ExecuteMessageUpdateAsync(string messageId, string assignments, CancellationToken cancellationToken)
    {
        return retryPolicy.ExecuteAsync(
            async ct =>
            {
                using var connection = connectionFactory.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = $"UPDATE {table} SET {assignments} WHERE \"MessageId\" = $messageId;";
                command.Parameters.Add(SqliteInboxSql.Parameter("$messageId", messageId));
                command.Parameters.Add(SqliteInboxSql.Parameter("$now", SqliteInboxSql.Timestamp(timeProvider.GetUtcNow())));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task EnqueueAsync(string topic, string source, string messageId, string payload, CancellationToken cancellationToken) =>
        EnqueueAsync(topic, source, messageId, payload, null, null, cancellationToken);

    /// <inheritdoc />
    public Task EnqueueAsync(string topic, string source, string messageId, string payload, byte[]? hash, CancellationToken cancellationToken) =>
        EnqueueAsync(topic, source, messageId, payload, hash, null, cancellationToken);

    /// <inheritdoc />
    public Task EnqueueAsync(
        string topic,
        string source,
        string messageId,
        string payload,
        byte[]? hash,
        DateTimeOffset? dueTimeUtc,
        CancellationToken cancellationToken)
    {
        ValidateMessage(messageId, source);
        return retryPolicy.ExecuteAsync(
            async ct =>
            {
                if (migrationRunner is not null)
                {
                    await SqliteInboxSchema.EnsureAsync(migrationRunner, tableName, ct).ConfigureAwait(false);
                }

                await EnqueueCoreAsync(topic, source, messageId, payload, hash, dueTimeUtc, ct).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private async Task EnqueueCoreAsync(
        string topic,
        string source,
        string messageId,
        string payload,
        byte[]? hash,
        DateTimeOffset? dueTimeUtc,
        CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.OpenConnection();
        using var command = connection.CreateCommand();
        var now = SqliteInboxSql.Timestamp(timeProvider.GetUtcNow());
        command.CommandText = $"""
            INSERT INTO {table} ("MessageId", "Source", "Topic", "Payload", "Hash", "DueTimeUtc", "FirstSeenUtc", "LastSeenUtc", "Attempts", "Status")
            VALUES ($messageId, $source, $topic, $payload, $hash, $dueTimeUtc, $now, $now, 1, 'Seen')
            ON CONFLICT ("MessageId") DO UPDATE SET
                "LastSeenUtc" = $now,
                "Attempts" = "Attempts" + 1,
                "Topic" = COALESCE(excluded."Topic", "Topic"),
                "Payload" = COALESCE(excluded."Payload", "Payload"),
                "DueTimeUtc" = COALESCE(excluded."DueTimeUtc", "DueTimeUtc");
            """;
        command.Parameters.Add(SqliteInboxSql.Parameter("$messageId", messageId));
        command.Parameters.Add(SqliteInboxSql.Parameter("$source", source));
        command.Parameters.Add(SqliteInboxSql.Parameter("$topic", topic));
        command.Parameters.Add(SqliteInboxSql.Parameter("$payload", payload));
        command.Parameters.Add(SqliteInboxSql.Parameter("$hash", hash));
        command.Parameters.Add(SqliteInboxSql.Parameter("$dueTimeUtc", dueTimeUtc.HasValue ? SqliteInboxSql.Timestamp(dueTimeUtc.Value) : null));
        command.Parameters.Add(SqliteInboxSql.Parameter("$now", now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateMessage(string messageId, string source)
    {
        ValidateMessageId(messageId);
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Source cannot be null or empty.", nameof(source));
        }
    }

    private static void ValidateMessageId(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            throw new ArgumentException("MessageId cannot be null or empty.", nameof(messageId));
        }
    }
}
