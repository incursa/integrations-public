// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>Creates the SQLite outbox and fan-in tables.</summary>
internal static class SqliteOutboxSchema
{
    internal const string OutboxTable = "\"Outbox\"";
    internal const string JoinTable = "\"OutboxJoin\"";
    internal const string JoinMemberTable = "\"OutboxJoinMember\"";

    internal static async Task EnsureAsync(SqliteConnectionFactory factory, SqliteRetryPolicy retry, CancellationToken cancellationToken)
    {
        await retry.ExecuteAsync(async ct =>
        {
            using var connection = factory.OpenConnection();
            await ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                await ExecuteAsync(connection, $"""
                    CREATE TABLE IF NOT EXISTS {OutboxTable} (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "Topic" TEXT NOT NULL,
                        "Payload" TEXT NOT NULL,
                        "CorrelationId" TEXT NULL,
                        "MessageId" TEXT NOT NULL UNIQUE,
                        "CreatedAt" TEXT NOT NULL,
                        "IsProcessed" INTEGER NOT NULL DEFAULT 0,
                        "ProcessedAt" TEXT NULL,
                        "ProcessedBy" TEXT NULL,
                        "RetryCount" INTEGER NOT NULL DEFAULT 0,
                        "LastError" TEXT NULL,
                        "DueTimeUtc" TEXT NULL,
                        "Status" INTEGER NOT NULL DEFAULT 0,
                        "OwnerToken" TEXT NULL,
                        "LockedUntil" TEXT NULL
                    );
                    CREATE INDEX IF NOT EXISTS "IX_Outbox_Due" ON {OutboxTable}("Status", "DueTimeUtc", "CreatedAt");
                    CREATE INDEX IF NOT EXISTS "IX_Outbox_Lock" ON {OutboxTable}("Status", "LockedUntil");
                    """, ct).ConfigureAwait(false);

                await ExecuteAsync(connection, $"""
                    CREATE TABLE IF NOT EXISTS {JoinTable} (
                        "JoinId" TEXT NOT NULL PRIMARY KEY,
                        "PayeWaiveTenantId" INTEGER NOT NULL,
                        "ExpectedSteps" INTEGER NOT NULL,
                        "CompletedSteps" INTEGER NOT NULL DEFAULT 0,
                        "FailedSteps" INTEGER NOT NULL DEFAULT 0,
                        "Status" INTEGER NOT NULL DEFAULT 0,
                        "CreatedUtc" TEXT NOT NULL,
                        "LastUpdatedUtc" TEXT NOT NULL,
                        "Metadata" TEXT NULL
                    );
                    CREATE INDEX IF NOT EXISTS "IX_OutboxJoin_TenantStatus" ON {JoinTable}("PayeWaiveTenantId", "Status");
                    """, ct).ConfigureAwait(false);

                await ExecuteAsync(connection, $"""
                    CREATE TABLE IF NOT EXISTS {JoinMemberTable} (
                        "JoinId" TEXT NOT NULL,
                        "OutboxMessageId" TEXT NOT NULL,
                        "CreatedUtc" TEXT NOT NULL,
                        "CompletedAt" TEXT NULL,
                        "FailedAt" TEXT NULL,
                        PRIMARY KEY ("JoinId", "OutboxMessageId"),
                        FOREIGN KEY ("JoinId") REFERENCES {JoinTable}("JoinId") ON DELETE CASCADE,
                        FOREIGN KEY ("OutboxMessageId") REFERENCES {OutboxTable}("MessageId") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS "IX_OutboxJoinMember_MessageId" ON {JoinMemberTable}("OutboxMessageId");
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

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
