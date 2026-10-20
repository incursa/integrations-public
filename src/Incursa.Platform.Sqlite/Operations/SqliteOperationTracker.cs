// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Incursa.Platform.Correlation;
using Incursa.Platform.Operations;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>Durable operation tracker backed by a single local SQLite database.</summary>
public sealed class SqliteOperationTracker : IOperationTracker
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;
    private readonly SqliteOperationOptions options;
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes an operation tracker over the supplied local SQLite store.</summary>
    public SqliteOperationTracker(
        SqliteConnectionFactory connectionFactory,
        SqliteRetryPolicy retryPolicy,
        SqliteOperationOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        this.options = options ?? new SqliteOperationOptions();
        this.options.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<OperationId> StartAsync(string name, CorrelationContext? correlationContext, OperationId? parentOperationId, IReadOnlyDictionary<string, string>? tags, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await SqliteOperationSchema.EnsureAsync(connectionFactory, retryPolicy, options, cancellationToken).ConfigureAwait(false);
        var operationId = OperationId.NewId();
        var now = timeProvider.GetUtcNow();
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteOperationSchema.ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                await SqliteOperationSchema.ExecuteAsync(connection, $"""
                    INSERT INTO {OperationsTable} (OperationId,Name,Status,StartedAtUtc,UpdatedAtUtc,CorrelationId,CausationId,TraceId,SpanId,CorrelationCreatedAtUtc,CorrelationTagsJson,ParentOperationId,TagsJson)
                    VALUES ($id,$name,$status,$started,$updated,$correlation,$causation,$trace,$span,$correlationCreated,$correlationTags,$parent,$tags);
                    """, ct, null,
                    ("$id", operationId.Value), ("$name", name.Trim()), ("$status", (int)OperationStatus.Pending),
                    ("$started", SqliteOperationSchema.Utc(now)), ("$updated", SqliteOperationSchema.Utc(now)),
                    ("$correlation", correlationContext?.CorrelationId.Value), ("$causation", correlationContext?.CausationId?.Value),
                    ("$trace", correlationContext?.TraceId), ("$span", correlationContext?.SpanId),
                    ("$correlationCreated", correlationContext is null ? null : SqliteOperationSchema.Utc(correlationContext.CreatedAtUtc)),
                    ("$correlationTags", SerializeTags(correlationContext?.Tags)), ("$parent", parentOperationId?.Value), ("$tags", SerializeTags(tags))).ConfigureAwait(false);
                await SqliteOperationSchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
            }
            catch
            {
                try { await SqliteOperationSchema.ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); } catch (SqliteException) { }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
        SqlitePlatformMetrics.RecordOperationStarted();
        return operationId;
    }

    /// <inheritdoc />
    public async Task UpdateProgressAsync(OperationId operationId, double? percentComplete, string? message, CancellationToken cancellationToken)
    {
        await SqliteOperationSchema.EnsureAsync(connectionFactory, retryPolicy, options, cancellationToken).ConfigureAwait(false);
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            var changed = await ExecuteCountAsync(connection, $"""
                UPDATE {OperationsTable}
                SET Status=CASE WHEN Status=$pending THEN $running ELSE Status END,
                    UpdatedAtUtc=$updated, PercentComplete=$percent, Message=$message, RowVersion=RowVersion+1
                WHERE OperationId=$id;
                """, ct, ("$pending", (int)OperationStatus.Pending), ("$running", (int)OperationStatus.Running),
                ("$updated", SqliteOperationSchema.Utc(timeProvider.GetUtcNow())), ("$percent", percentComplete is null ? null : Math.Clamp(percentComplete.Value, 0, 100)),
                ("$message", string.IsNullOrWhiteSpace(message) ? null : message.Trim()), ("$id", operationId.Value)).ConfigureAwait(false);
            if (changed == 0) throw new InvalidOperationException($"Operation '{operationId}' was not found.");
        }, cancellationToken).ConfigureAwait(false);
        SqlitePlatformMetrics.RecordOperationProgressUpdated();
    }

    /// <inheritdoc />
    public async Task AddEventAsync(OperationId operationId, string kind, string message, string? dataJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        await SqliteOperationSchema.EnsureAsync(connectionFactory, retryPolicy, options, cancellationToken).ConfigureAwait(false);
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteOperationSchema.ExecuteAsync(connection, $"""
                INSERT INTO {EventsTable}(OperationId,OccurredAtUtc,Kind,Message,DataJson)
                VALUES ($id,$occurred,$kind,$message,$data);
                """, ct, null, ("$id", operationId.Value), ("$occurred", SqliteOperationSchema.Utc(timeProvider.GetUtcNow())),
                ("$kind", kind.Trim()), ("$message", message.Trim()), ("$data", dataJson)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        SqlitePlatformMetrics.RecordOperationEvent(kind.Trim());
    }

    /// <inheritdoc />
    public async Task CompleteAsync(OperationId operationId, OperationStatus status, string? message, CancellationToken cancellationToken)
    {
        await SqliteOperationSchema.EnsureAsync(connectionFactory, retryPolicy, options, cancellationToken).ConfigureAwait(false);
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            var now = timeProvider.GetUtcNow();
            var changed = await ExecuteCountAsync(connection, $"""
                UPDATE {OperationsTable}
                SET Status=$status, UpdatedAtUtc=$updated, CompletedAtUtc=$completed, Message=$message, RowVersion=RowVersion+1
                WHERE OperationId=$id;
                """, ct, ("$status", (int)status), ("$updated", SqliteOperationSchema.Utc(now)),
                ("$completed", IsTerminal(status) ? SqliteOperationSchema.Utc(now) : null),
                ("$message", string.IsNullOrWhiteSpace(message) ? null : message.Trim()), ("$id", operationId.Value)).ConfigureAwait(false);
            if (changed == 0) throw new InvalidOperationException($"Operation '{operationId}' was not found.");
        }, cancellationToken).ConfigureAwait(false);
        SqlitePlatformMetrics.RecordOperationCompleted(status.ToString());
    }

    /// <inheritdoc />
    public async Task<OperationSnapshot?> GetSnapshotAsync(OperationId operationId, CancellationToken cancellationToken)
    {
        await SqliteOperationSchema.EnsureAsync(connectionFactory, retryPolicy, options, cancellationToken).ConfigureAwait(false);
        var snapshot = await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT OperationId,Name,Status,StartedAtUtc,UpdatedAtUtc,CompletedAtUtc,PercentComplete,Message,CorrelationId,CausationId,TraceId,SpanId,CorrelationCreatedAtUtc,CorrelationTagsJson,ParentOperationId,TagsJson FROM {OperationsTable} WHERE OperationId=$id;";
            command.Parameters.AddWithValue("$id", operationId.Value);
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return !await reader.ReadAsync(ct).ConfigureAwait(false) ? null : MapSnapshot(reader);
        }, cancellationToken).ConfigureAwait(false);
        SqlitePlatformMetrics.RecordOperationSnapshotRead(snapshot is not null);
        return snapshot;
    }

    private string OperationsTable => SqliteIdentifier.Quote(options.OperationsTable);
    private string EventsTable => SqliteIdentifier.Quote(options.OperationEventsTable);

    private static bool IsTerminal(OperationStatus status) => status is OperationStatus.Succeeded or OperationStatus.Failed or OperationStatus.Canceled or OperationStatus.Stalled;

    private static async Task<int> ExecuteCountAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static OperationSnapshot MapSnapshot(SqliteDataReader reader)
    {
        var correlationId = reader.IsDBNull(8) ? null : reader.GetString(8);
        CorrelationContext? correlation = correlationId is null ? null : new CorrelationContext(new CorrelationId(correlationId), reader.IsDBNull(9) ? null : new CorrelationId(reader.GetString(9)), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), SqliteOperationSchema.ReadUtc(reader, 12) ?? DateTimeOffset.UnixEpoch, DeserializeTags(reader.IsDBNull(13) ? null : reader.GetString(13)));
        return new OperationSnapshot(new OperationId(reader.GetString(0)), reader.GetString(1), (OperationStatus)reader.GetInt32(2), SqliteOperationSchema.ReadUtc(reader, 3)!.Value, SqliteOperationSchema.ReadUtc(reader, 4)!.Value, SqliteOperationSchema.ReadUtc(reader, 5), reader.IsDBNull(6) ? null : reader.GetDouble(6), reader.IsDBNull(7) ? null : reader.GetString(7), correlation, reader.IsDBNull(14) ? null : new OperationId(reader.GetString(14)), DeserializeTags(reader.IsDBNull(15) ? null : reader.GetString(15)));
    }

    private static string? SerializeTags(IReadOnlyDictionary<string, string>? tags) => tags is null ? null : JsonSerializer.Serialize(tags);

    private static Dictionary<string, string>? DeserializeTags(string? json) => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(json);
}
