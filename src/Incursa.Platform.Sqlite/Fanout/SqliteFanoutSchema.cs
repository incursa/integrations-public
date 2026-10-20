// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>
/// Owns the SQLite fanout tables. SQLite has one local logical schema, so the
/// table names are deliberately fixed and do not support per-host routing.
/// </summary>
internal static class SqliteFanoutSchema
{
    internal const string PolicyTable = "FanoutPolicy";
    internal const string CursorTable = "FanoutCursor";

    internal static async Task EnsureAsync(
        SqliteConnectionFactory connectionFactory,
        SqliteRetryPolicy retryPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(retryPolicy);

        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                await ExecuteAsync(connection, $"""
                    CREATE TABLE IF NOT EXISTS {PolicyTable} (
                        FanoutTopic TEXT NOT NULL,
                        WorkKey TEXT NOT NULL,
                        DefaultEverySeconds INTEGER NOT NULL CHECK (DefaultEverySeconds > 0),
                        JitterSeconds INTEGER NOT NULL CHECK (JitterSeconds >= 0),
                        UpdatedAtUtc TEXT NOT NULL,
                        PRIMARY KEY (FanoutTopic, WorkKey)
                    );
                    CREATE TABLE IF NOT EXISTS {CursorTable} (
                        FanoutTopic TEXT NOT NULL,
                        WorkKey TEXT NOT NULL,
                        ShardKey TEXT NOT NULL,
                        LastCompletedAtUtc TEXT NOT NULL,
                        UpdatedAtUtc TEXT NOT NULL,
                        PRIMARY KEY (FanoutTopic, WorkKey, ShardKey)
                    );
                    """, ct).ConfigureAwait(false);
                await ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false);
                }
                catch (SqliteException)
                {
                    // Preserve the original schema failure.
                }

                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    internal static DateTimeOffset ReadUtc(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    internal static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
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
