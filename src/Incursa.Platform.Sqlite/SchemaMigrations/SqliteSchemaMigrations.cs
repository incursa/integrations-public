// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>Builds the complete SQLite schema bundle for one local database.</summary>
/// <remarks>
/// SQLite has one logical schema and no server-side stored procedures.  Each
/// migration is therefore ordinary transactional SQL and is journaled by
/// <see cref="SqliteMigrationRunner"/>.  Feature stores may still lazily call
/// their local CREATE TABLE safeguards, which are harmless after this bundle
/// has been applied.
/// </remarks>
public static class SqliteSchemaMigrations
{
    /// <summary>Gets the enabled provider migrations in deterministic order.</summary>
    public static IReadOnlyList<SqliteMigration> GetMigrations(SqlitePlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var migrations = new List<SqliteMigration>
        {
            new("sqlite-provider-001", "CREATE TABLE IF NOT EXISTS \"SqliteProviderMetadata\" (\"Key\" TEXT NOT NULL PRIMARY KEY, \"Value\" TEXT NOT NULL);")
        };

        if (options.EnableLeases && options.Leases.EnableSchemaDeployment)
        {
            migrations.Add(SqliteLeaseSchema.GetMigration(options.Leases.TableName));
        }

        if (options.EnableInbox && options.Inbox.EnableSchemaDeployment)
        {
            migrations.AddRange(SqliteInboxSchema.GetMigrations(options.Inbox.TableName));
        }

        if (options.EnableScheduling)
        {
            migrations.Add(new SqliteMigration("sqlite-scheduler-001", SchedulerSql));
        }

        if (options.EnableOutbox)
        {
            migrations.Add(new SqliteMigration("sqlite-outbox-001", OutboxSql));
        }

        if (options.EnableFanout)
        {
            migrations.Add(new SqliteMigration("sqlite-fanout-001", FanoutSql));
        }

        if (options.EnableIdempotency && options.Idempotency.EnableSchemaDeployment)
        {
            migrations.Add(SqliteIdempotencySchema.GetMigration(options.Idempotency.TableName));
        }

        if (options.EnableOperations && options.Operations.EnableSchemaDeployment)
        {
            migrations.Add(new SqliteMigration("sqlite-operations-001", OperationsSql(options.Operations)));
        }

        if (options.EnableAudit && options.Audit.EnableSchemaDeployment)
        {
            migrations.Add(new SqliteMigration("sqlite-audit-001", AuditSql(options.Audit)));
        }

        return migrations;
    }

    /// <summary>Applies all enabled migrations atomically.</summary>
    public static Task ApplyAsync(SqliteMigrationRunner runner, SqlitePlatformOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.ApplyAsync(GetMigrations(options), cancellationToken);
    }

    private const string SchedulerSql = """
        CREATE TABLE IF NOT EXISTS "SqliteTimers" (
            Id TEXT NOT NULL PRIMARY KEY, Topic TEXT NOT NULL, Payload TEXT NOT NULL,
            DueTimeUtc TEXT NOT NULL, Status INTEGER NOT NULL DEFAULT 0,
            OwnerToken TEXT NULL, LockedUntilUtc TEXT NULL, RetryCount INTEGER NOT NULL DEFAULT 0,
            CreatedAtUtc TEXT NOT NULL, ProcessedAtUtc TEXT NULL, LastError TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_SqliteTimers_Ready" ON "SqliteTimers"(Status, DueTimeUtc, LockedUntilUtc, CreatedAtUtc);
        CREATE TABLE IF NOT EXISTS "SqliteJobs" (
            Id TEXT NOT NULL PRIMARY KEY, JobName TEXT NOT NULL UNIQUE, Topic TEXT NOT NULL,
            CronSchedule TEXT NOT NULL, Payload TEXT NULL, NextDueTimeUtc TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_SqliteJobs_NextDue" ON "SqliteJobs"(NextDueTimeUtc);
        CREATE TABLE IF NOT EXISTS "SqliteJobRuns" (
            Id TEXT NOT NULL PRIMARY KEY, JobId TEXT NOT NULL, ScheduledTimeUtc TEXT NOT NULL,
            Status INTEGER NOT NULL DEFAULT 0, OwnerToken TEXT NULL, LockedUntilUtc TEXT NULL,
            RetryCount INTEGER NOT NULL DEFAULT 0, ProcessedAtUtc TEXT NULL, LastError TEXT NULL,
            FOREIGN KEY (JobId) REFERENCES "SqliteJobs"(Id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_SqliteJobRuns_Ready" ON "SqliteJobRuns"(Status, ScheduledTimeUtc, LockedUntilUtc);
        CREATE TABLE IF NOT EXISTS "SqliteSchedulerState" (
            Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1), CurrentFencingToken INTEGER NOT NULL DEFAULT 0, LastRunAtUtc TEXT NULL
        );
        INSERT OR IGNORE INTO "SqliteSchedulerState"(Id, CurrentFencingToken) VALUES (1, 0);
        """;

    private const string OutboxSql = """
        CREATE TABLE IF NOT EXISTS "Outbox" (
            "Id" TEXT NOT NULL PRIMARY KEY, "Topic" TEXT NOT NULL, "Payload" TEXT NOT NULL,
            "CorrelationId" TEXT NULL, "MessageId" TEXT NOT NULL UNIQUE, "CreatedAt" TEXT NOT NULL,
            "IsProcessed" INTEGER NOT NULL DEFAULT 0, "ProcessedAt" TEXT NULL, "ProcessedBy" TEXT NULL,
            "RetryCount" INTEGER NOT NULL DEFAULT 0, "LastError" TEXT NULL, "DueTimeUtc" TEXT NULL,
            "Status" INTEGER NOT NULL DEFAULT 0, "OwnerToken" TEXT NULL, "LockedUntil" TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_Outbox_Due" ON "Outbox"("Status", "DueTimeUtc", "CreatedAt");
        CREATE INDEX IF NOT EXISTS "IX_Outbox_Lock" ON "Outbox"("Status", "LockedUntil");
        CREATE TABLE IF NOT EXISTS "OutboxJoin" (
            "JoinId" TEXT NOT NULL PRIMARY KEY, "PayeWaiveTenantId" INTEGER NOT NULL,
            "ExpectedSteps" INTEGER NOT NULL, "CompletedSteps" INTEGER NOT NULL DEFAULT 0,
            "FailedSteps" INTEGER NOT NULL DEFAULT 0, "Status" INTEGER NOT NULL DEFAULT 0,
            "CreatedUtc" TEXT NOT NULL, "LastUpdatedUtc" TEXT NOT NULL, "Metadata" TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_OutboxJoin_TenantStatus" ON "OutboxJoin"("PayeWaiveTenantId", "Status");
        CREATE TABLE IF NOT EXISTS "OutboxJoinMember" (
            "JoinId" TEXT NOT NULL, "OutboxMessageId" TEXT NOT NULL, "CreatedUtc" TEXT NOT NULL,
            "CompletedAt" TEXT NULL, "FailedAt" TEXT NULL, PRIMARY KEY ("JoinId", "OutboxMessageId"),
            FOREIGN KEY ("JoinId") REFERENCES "OutboxJoin"("JoinId") ON DELETE CASCADE,
            FOREIGN KEY ("OutboxMessageId") REFERENCES "Outbox"("MessageId") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_OutboxJoinMember_MessageId" ON "OutboxJoinMember"("OutboxMessageId");
        """;

    private const string FanoutSql = """
        CREATE TABLE IF NOT EXISTS "FanoutPolicy" (
            FanoutTopic TEXT NOT NULL, WorkKey TEXT NOT NULL, DefaultEverySeconds INTEGER NOT NULL CHECK (DefaultEverySeconds > 0),
            JitterSeconds INTEGER NOT NULL CHECK (JitterSeconds >= 0), UpdatedAtUtc TEXT NOT NULL,
            PRIMARY KEY (FanoutTopic, WorkKey)
        );
        CREATE TABLE IF NOT EXISTS "FanoutCursor" (
            FanoutTopic TEXT NOT NULL, WorkKey TEXT NOT NULL, ShardKey TEXT NOT NULL,
            LastCompletedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
            PRIMARY KEY (FanoutTopic, WorkKey, ShardKey)
        );
        """;

    private static string OperationsSql(SqliteOperationOptions options) => $"""
        CREATE TABLE IF NOT EXISTS {SqliteIdentifier.Quote(options.OperationsTable)} (
            OperationId TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Status INTEGER NOT NULL,
            StartedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT NULL,
            PercentComplete REAL NULL, Message TEXT NULL, CorrelationId TEXT NULL, CausationId TEXT NULL,
            TraceId TEXT NULL, SpanId TEXT NULL, CorrelationCreatedAtUtc TEXT NULL, CorrelationTagsJson TEXT NULL,
            ParentOperationId TEXT NULL, TagsJson TEXT NULL, RowVersion INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS {SqliteIdentifier.Quote("IX_" + options.OperationsTable + "_Status_UpdatedAtUtc")}
            ON {SqliteIdentifier.Quote(options.OperationsTable)}(Status, UpdatedAtUtc);
        CREATE INDEX IF NOT EXISTS {SqliteIdentifier.Quote("IX_" + options.OperationsTable + "_ParentOperationId")}
            ON {SqliteIdentifier.Quote(options.OperationsTable)}(ParentOperationId);
        CREATE TABLE IF NOT EXISTS {SqliteIdentifier.Quote(options.OperationEventsTable)} (
            EventId INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, OperationId TEXT NOT NULL,
            OccurredAtUtc TEXT NOT NULL, Kind TEXT NOT NULL, Message TEXT NOT NULL, DataJson TEXT NULL,
            FOREIGN KEY (OperationId) REFERENCES {SqliteIdentifier.Quote(options.OperationsTable)}(OperationId) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS {SqliteIdentifier.Quote("IX_" + options.OperationEventsTable + "_OperationId_OccurredAtUtc")}
            ON {SqliteIdentifier.Quote(options.OperationEventsTable)}(OperationId, OccurredAtUtc);
        """;

    private static string AuditSql(SqliteAuditOptions options) => $"""
        CREATE TABLE IF NOT EXISTS {SqliteIdentifier.Quote(options.AuditEventsTable)} (
            AuditEventId TEXT NOT NULL PRIMARY KEY, OccurredAtUtc TEXT NOT NULL, Name TEXT NOT NULL,
            DisplayMessage TEXT NOT NULL, Outcome INTEGER NOT NULL, DataJson TEXT NULL,
            ActorType TEXT NULL, ActorId TEXT NULL, ActorDisplay TEXT NULL, CorrelationId TEXT NULL,
            CausationId TEXT NULL, TraceId TEXT NULL, SpanId TEXT NULL, CorrelationCreatedAtUtc TEXT NULL,
            CorrelationTagsJson TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS {SqliteIdentifier.Quote("IX_" + options.AuditEventsTable + "_OccurredAtUtc")}
            ON {SqliteIdentifier.Quote(options.AuditEventsTable)}(OccurredAtUtc DESC);
        CREATE TABLE IF NOT EXISTS {SqliteIdentifier.Quote(options.AuditAnchorsTable)} (
            AuditEventId TEXT NOT NULL, AnchorType TEXT NOT NULL, AnchorId TEXT NOT NULL, Role TEXT NOT NULL,
            PRIMARY KEY (AuditEventId, AnchorType, AnchorId, Role),
            FOREIGN KEY (AuditEventId) REFERENCES {SqliteIdentifier.Quote(options.AuditEventsTable)}(AuditEventId) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS {SqliteIdentifier.Quote("IX_" + options.AuditAnchorsTable + "_Type_Id")}
            ON {SqliteIdentifier.Quote(options.AuditAnchorsTable)}(AnchorType, AnchorId, Role);
        """;
}
