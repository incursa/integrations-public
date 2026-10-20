// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>Creates and reads the SQLite operation tracking schema.</summary>
internal static class SqliteOperationSchema
{
    internal static async Task EnsureAsync(
        SqliteConnectionFactory connectionFactory,
        SqliteRetryPolicy retryPolicy,
        SqliteOperationOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.EnableSchemaDeployment)
        {
            return;
        }

        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                var operations = SqliteIdentifier.Quote(options.OperationsTable);
                var events = SqliteIdentifier.Quote(options.OperationEventsTable);
                await ExecuteAsync(connection, $"""
                    CREATE TABLE IF NOT EXISTS {operations} (
                        OperationId TEXT NOT NULL PRIMARY KEY,
                        Name TEXT NOT NULL,
                        Status INTEGER NOT NULL,
                        StartedAtUtc TEXT NOT NULL,
                        UpdatedAtUtc TEXT NOT NULL,
                        CompletedAtUtc TEXT NULL,
                        PercentComplete REAL NULL,
                        Message TEXT NULL,
                        CorrelationId TEXT NULL,
                        CausationId TEXT NULL,
                        TraceId TEXT NULL,
                        SpanId TEXT NULL,
                        CorrelationCreatedAtUtc TEXT NULL,
                        CorrelationTagsJson TEXT NULL,
                        ParentOperationId TEXT NULL,
                        TagsJson TEXT NULL,
                        RowVersion INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS {SqliteIdentifier.Quote("IX_" + options.OperationsTable + "_Status_UpdatedAtUtc")}
                        ON {operations}(Status, UpdatedAtUtc);
                    CREATE INDEX IF NOT EXISTS {SqliteIdentifier.Quote("IX_" + options.OperationsTable + "_ParentOperationId")}
                        ON {operations}(ParentOperationId);
                    CREATE TABLE IF NOT EXISTS {events} (
                        EventId INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        OperationId TEXT NOT NULL,
                        OccurredAtUtc TEXT NOT NULL,
                        Kind TEXT NOT NULL,
                        Message TEXT NOT NULL,
                        DataJson TEXT NULL,
                        FOREIGN KEY (OperationId) REFERENCES {operations}(OperationId) ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS {SqliteIdentifier.Quote("IX_" + options.OperationEventsTable + "_OperationId_OccurredAtUtc")}
                        ON {events}(OperationId, OccurredAtUtc);
                    """, ct).ConfigureAwait(false);
                await ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
            }
            catch
            {
                try { await ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); }
                catch (SqliteException) { }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    internal static DateTimeOffset? ReadUtc(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return DateTimeOffset.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
    }

    internal static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
