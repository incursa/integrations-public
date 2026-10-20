// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>SQLite schema and migration helpers for idempotency records.</summary>
public static class SqliteIdempotencySchema
{
    /// <summary>Returns the migration that creates the idempotency table.</summary>
    public static SqliteMigration GetMigration(string tableName = "SqliteIdempotency")
    {
        SqliteIdentifier.Validate(tableName, nameof(tableName));
        var table = SqliteIdentifier.Quote(tableName);
        return new SqliteMigration(
            "sqlite-idempotency-001",
            $"""
            CREATE TABLE IF NOT EXISTS {table} (
                IdempotencyKey TEXT NOT NULL PRIMARY KEY,
                Status INTEGER NOT NULL,
                LockedUntilUtc TEXT NULL,
                LockedBy TEXT NULL,
                FailureCount INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS {SqliteIdentifier.Quote("IX_" + tableName + "_Cleanup")} ON {table}(Status, CompletedAtUtc, UpdatedAtUtc);
            """);
    }

    internal static async Task EnsureAsync(SqliteConnectionFactory factory, SqliteRetryPolicy retry, string tableName, CancellationToken cancellationToken)
    {
        var migration = GetMigration(tableName);
        await retry.ExecuteAsync(async ct =>
        {
            using var connection = factory.OpenConnection();
            await ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                await ExecuteAsync(connection, migration.Sql, ct).ConfigureAwait(false);
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

    internal static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    internal static DateTimeOffset? ParseUtc(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        return DateTimeOffset.Parse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
