// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

internal static class SqliteAuditSchema
{
    internal static async Task EnsureAsync(SqliteConnectionFactory connectionFactory, SqliteRetryPolicy retryPolicy, SqliteAuditOptions options, CancellationToken cancellationToken)
    {
        if (!options.EnableSchemaDeployment) return;
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                var events = SqliteIdentifier.Quote(options.AuditEventsTable);
                var anchors = SqliteIdentifier.Quote(options.AuditAnchorsTable);
                await ExecuteAsync(connection, $"""
                    CREATE TABLE IF NOT EXISTS {events} (
                        AuditEventId TEXT NOT NULL PRIMARY KEY,
                        OccurredAtUtc TEXT NOT NULL,
                        Name TEXT NOT NULL,
                        DisplayMessage TEXT NOT NULL,
                        Outcome INTEGER NOT NULL,
                        DataJson TEXT NULL,
                        ActorType TEXT NULL,
                        ActorId TEXT NULL,
                        ActorDisplay TEXT NULL,
                        CorrelationId TEXT NULL,
                        CausationId TEXT NULL,
                        TraceId TEXT NULL,
                        SpanId TEXT NULL,
                        CorrelationCreatedAtUtc TEXT NULL,
                        CorrelationTagsJson TEXT NULL
                    );
                    CREATE INDEX IF NOT EXISTS {SqliteIdentifier.Quote("IX_" + options.AuditEventsTable + "_OccurredAtUtc")} ON {events}(OccurredAtUtc DESC);
                    CREATE INDEX IF NOT EXISTS {SqliteIdentifier.Quote("IX_" + options.AuditEventsTable + "_Name_OccurredAtUtc")} ON {events}(Name, OccurredAtUtc DESC);
                    CREATE TABLE IF NOT EXISTS {anchors} (
                        AuditEventId TEXT NOT NULL,
                        AnchorType TEXT NOT NULL,
                        AnchorId TEXT NOT NULL,
                        Role TEXT NOT NULL,
                        PRIMARY KEY (AuditEventId, AnchorType, AnchorId, Role),
                        FOREIGN KEY (AuditEventId) REFERENCES {events}(AuditEventId) ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS {SqliteIdentifier.Quote("IX_" + options.AuditAnchorsTable + "_Type_Id")} ON {anchors}(AnchorType, AnchorId, Role);
                    """, ct).ConfigureAwait(false);
                await ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
            }
            catch
            {
                try { await ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); } catch (SqliteException) { }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
