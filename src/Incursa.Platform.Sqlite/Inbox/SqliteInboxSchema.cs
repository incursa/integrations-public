// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>Builds the transactional SQLite inbox schema migrations.</summary>
public static class SqliteInboxSchema
{
    /// <summary>Gets the inbox migrations for the supplied table name.</summary>
    /// <param name="tableName">The SQLite table name.</param>
    /// <returns>The ordered inbox migrations.</returns>
    public static IReadOnlyList<SqliteMigration> GetMigrations(string tableName = "Inbox")
    {
        SqliteIdentifier.Validate(tableName, nameof(tableName));
        var table = SqliteIdentifier.Quote(tableName);

        return
        [
            new SqliteMigration(
                "inbox-001",
                $"""
                CREATE TABLE IF NOT EXISTS {table} (
                    "MessageId" TEXT NOT NULL PRIMARY KEY,
                    "Source" TEXT NOT NULL,
                    "Hash" BLOB NULL,
                    "FirstSeenUtc" TEXT NOT NULL,
                    "LastSeenUtc" TEXT NOT NULL,
                    "ProcessedUtc" TEXT NULL,
                    "DueTimeUtc" TEXT NULL,
                    "Attempts" INTEGER NOT NULL DEFAULT 0,
                    "Status" TEXT NOT NULL DEFAULT 'Seen',
                    "LastError" TEXT NULL,
                    "LockedUntil" TEXT NULL,
                    "OwnerToken" TEXT NULL,
                    "Topic" TEXT NULL,
                    "Payload" TEXT NULL,
                    CONSTRAINT "CK_{tableName}_Status" CHECK ("Status" IN ('Seen', 'Processing', 'Done', 'Dead'))
                );
                CREATE INDEX IF NOT EXISTS "IX_{tableName}_WorkQueue"
                    ON {table} ("Status", "DueTimeUtc", "LastSeenUtc");
                CREATE INDEX IF NOT EXISTS "IX_{tableName}_ProcessedUtc"
                    ON {table} ("Status", "ProcessedUtc");
                """)
        ];
    }

    /// <summary>Applies the inbox migrations using the configured migration runner.</summary>
    public static Task EnsureAsync(
        SqliteMigrationRunner runner,
        string tableName = "Inbox",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.ApplyAsync(GetMigrations(tableName), cancellationToken);
    }
}
