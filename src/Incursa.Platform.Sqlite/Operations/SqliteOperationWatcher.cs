// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform.Operations;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>Detects and marks stalled operations in a local SQLite store.</summary>
public sealed class SqliteOperationWatcher : IOperationWatcher
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;
    private readonly SqliteOperationOptions options;
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes an operation watcher over the supplied local SQLite store.</summary>
    public SqliteOperationWatcher(SqliteConnectionFactory connectionFactory, SqliteRetryPolicy retryPolicy, SqliteOperationOptions? options = null, TimeProvider? timeProvider = null)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        this.options = options ?? new SqliteOperationOptions();
        this.options.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OperationSnapshot>> FindStalledAsync(TimeSpan threshold, CancellationToken cancellationToken)
    {
        if (threshold < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(threshold));
        await SqliteOperationSchema.EnsureAsync(connectionFactory, retryPolicy, options, cancellationToken).ConfigureAwait(false);
        return await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT OperationId,Name,Status,StartedAtUtc,UpdatedAtUtc,CompletedAtUtc,PercentComplete,Message,CorrelationId,CausationId,TraceId,SpanId,CorrelationCreatedAtUtc,CorrelationTagsJson,ParentOperationId,TagsJson FROM {SqliteIdentifier.Quote(options.OperationsTable)} WHERE UpdatedAtUtc < $cutoff AND Status IN ($pending,$running) ORDER BY UpdatedAtUtc;";
            command.Parameters.AddWithValue("$cutoff", SqliteOperationSchema.Utc(timeProvider.GetUtcNow().Subtract(threshold)));
            command.Parameters.AddWithValue("$pending", (int)OperationStatus.Pending);
            command.Parameters.AddWithValue("$running", (int)OperationStatus.Running);
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<OperationSnapshot>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(SqliteOperationTracker.MapSnapshot(reader));
            return (IReadOnlyList<OperationSnapshot>)result;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkStalledAsync(OperationId operationId, CancellationToken cancellationToken)
    {
        await SqliteOperationSchema.EnsureAsync(connectionFactory, retryPolicy, options, cancellationToken).ConfigureAwait(false);
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteOperationSchema.ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                var changed = await ExecuteCountAsync(connection, $"UPDATE {SqliteIdentifier.Quote(options.OperationsTable)} SET Status=$status,UpdatedAtUtc=$updated,CompletedAtUtc=$updated,RowVersion=RowVersion+1 WHERE OperationId=$id AND Status IN ($pending,$running);", ct, ("$status", (int)OperationStatus.Stalled), ("$updated", SqliteOperationSchema.Utc(timeProvider.GetUtcNow())), ("$id", operationId.Value), ("$pending", (int)OperationStatus.Pending), ("$running", (int)OperationStatus.Running)).ConfigureAwait(false);
                if (changed == 0)
                {
                    using var exists = connection.CreateCommand();
                    exists.CommandText = $"SELECT COUNT(*) FROM {SqliteIdentifier.Quote(options.OperationsTable)} WHERE OperationId=$id;";
                    exists.Parameters.AddWithValue("$id", operationId.Value);
                    if (Convert.ToInt32(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 0) throw new InvalidOperationException($"Operation '{operationId}' was not found.");
                }

                await SqliteOperationSchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
            }
            catch
            {
                try { await SqliteOperationSchema.ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); } catch (SqliteException) { }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteCountAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
