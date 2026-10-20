// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>Schema and small SQL helpers shared by the SQLite scheduler components.</summary>
internal static class SqliteSchedulerSchema
{
    internal const string TimersTable = "SqliteTimers";
    internal const string JobsTable = "SqliteJobs";
    internal const string JobRunsTable = "SqliteJobRuns";
    internal const string StateTable = "SqliteSchedulerState";

    internal const int Ready = 0;
    internal const int InProgress = 1;
    internal const int Done = 2;
    internal const int Failed = 3;

    /// <summary>Returns the provider migration consumed by the root SQLite registration.</summary>
    internal static IReadOnlyList<SqliteMigration> Migrations => new[]
    {
        new SqliteMigration("sqlite-scheduler-v1", $"""
            CREATE TABLE IF NOT EXISTS {TimersTable} (
                Id TEXT NOT NULL PRIMARY KEY, Topic TEXT NOT NULL, Payload TEXT NOT NULL,
                DueTimeUtc TEXT NOT NULL, Status INTEGER NOT NULL DEFAULT 0, OwnerToken TEXT NULL,
                LockedUntilUtc TEXT NULL, RetryCount INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL, ProcessedAtUtc TEXT NULL, LastError TEXT NULL);
            CREATE INDEX IF NOT EXISTS IX_{TimersTable}_Ready ON {TimersTable}(Status, DueTimeUtc, LockedUntilUtc, CreatedAtUtc);
            CREATE TABLE IF NOT EXISTS {JobsTable} (
                Id TEXT NOT NULL PRIMARY KEY, JobName TEXT NOT NULL UNIQUE, Topic TEXT NOT NULL,
                CronSchedule TEXT NOT NULL, Payload TEXT NULL, NextDueTimeUtc TEXT NULL);
            CREATE INDEX IF NOT EXISTS IX_{JobsTable}_NextDue ON {JobsTable}(NextDueTimeUtc);
            CREATE TABLE IF NOT EXISTS {JobRunsTable} (
                Id TEXT NOT NULL PRIMARY KEY, JobId TEXT NOT NULL, ScheduledTimeUtc TEXT NOT NULL,
                Status INTEGER NOT NULL DEFAULT 0, OwnerToken TEXT NULL, LockedUntilUtc TEXT NULL,
                RetryCount INTEGER NOT NULL DEFAULT 0, ProcessedAtUtc TEXT NULL, LastError TEXT NULL,
                FOREIGN KEY (JobId) REFERENCES {JobsTable}(Id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_{JobRunsTable}_Ready ON {JobRunsTable}(Status, ScheduledTimeUtc, LockedUntilUtc);
            CREATE TABLE IF NOT EXISTS {StateTable} (
                Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1), CurrentFencingToken INTEGER NOT NULL DEFAULT 0,
                LastRunAtUtc TEXT NULL);
            INSERT OR IGNORE INTO {StateTable}(Id, CurrentFencingToken) VALUES (1, 0);
            """)
    };

    internal static async Task EnsureAsync(
        SqliteConnectionFactory connectionFactory,
        SqliteRetryPolicy retryPolicy,
        CancellationToken cancellationToken)
    {
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                await ExecuteAsync(connection, $"""
                    CREATE TABLE IF NOT EXISTS {TimersTable} (
                        Id TEXT NOT NULL PRIMARY KEY,
                        Topic TEXT NOT NULL,
                        Payload TEXT NOT NULL,
                        DueTimeUtc TEXT NOT NULL,
                        Status INTEGER NOT NULL DEFAULT 0,
                        OwnerToken TEXT NULL,
                        LockedUntilUtc TEXT NULL,
                        RetryCount INTEGER NOT NULL DEFAULT 0,
                        CreatedAtUtc TEXT NOT NULL,
                        ProcessedAtUtc TEXT NULL,
                        LastError TEXT NULL
                    );
                    CREATE INDEX IF NOT EXISTS IX_{TimersTable}_Ready
                        ON {TimersTable}(Status, DueTimeUtc, LockedUntilUtc, CreatedAtUtc);
                    CREATE TABLE IF NOT EXISTS {JobsTable} (
                        Id TEXT NOT NULL PRIMARY KEY,
                        JobName TEXT NOT NULL UNIQUE,
                        Topic TEXT NOT NULL,
                        CronSchedule TEXT NOT NULL,
                        Payload TEXT NULL,
                        NextDueTimeUtc TEXT NULL
                    );
                    CREATE INDEX IF NOT EXISTS IX_{JobsTable}_NextDue
                        ON {JobsTable}(NextDueTimeUtc);
                    CREATE TABLE IF NOT EXISTS {JobRunsTable} (
                        Id TEXT NOT NULL PRIMARY KEY,
                        JobId TEXT NOT NULL,
                        ScheduledTimeUtc TEXT NOT NULL,
                        Status INTEGER NOT NULL DEFAULT 0,
                        OwnerToken TEXT NULL,
                        LockedUntilUtc TEXT NULL,
                        RetryCount INTEGER NOT NULL DEFAULT 0,
                        ProcessedAtUtc TEXT NULL,
                        LastError TEXT NULL,
                        FOREIGN KEY (JobId) REFERENCES {JobsTable}(Id) ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS IX_{JobRunsTable}_Ready
                        ON {JobRunsTable}(Status, ScheduledTimeUtc, LockedUntilUtc);
                    CREATE TABLE IF NOT EXISTS {StateTable} (
                        Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                        CurrentFencingToken INTEGER NOT NULL DEFAULT 0,
                        LastRunAtUtc TEXT NULL
                    );
                    INSERT OR IGNORE INTO {StateTable}(Id, CurrentFencingToken) VALUES (1, 0);
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

    internal static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    internal static DateTimeOffset ReadUtc(SqliteDataReader reader, int ordinal)
    {
        return DateTimeOffset.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, parameterValue) in parameters)
        {
            command.Parameters.AddWithValue(name, parameterValue ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<T?> ScalarAsync<T>(SqliteConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, parameterValue) in parameters)
        {
            command.Parameters.AddWithValue(name, parameterValue ?? DBNull.Value);
        }

        var scalarValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (scalarValue is null or DBNull)
        {
            return default;
        }

        return (T)Convert.ChangeType(scalarValue, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
