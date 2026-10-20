// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>Options for the reusable SQLite storage foundation.</summary>
public sealed class SqlitePlatformOptions
{
    /// <summary>Gets or sets the local database file path.</summary>
    public required string DataSource { get; set; }

    /// <summary>Gets or sets the SQLite busy timeout in milliseconds.</summary>
    public int BusyTimeoutMilliseconds { get; set; } = 5_000;

    /// <summary>Gets or sets the maximum number of retries after a busy or locked error.</summary>
    public int MaxRetryCount { get; set; } = 5;

    /// <summary>Gets or sets the initial retry delay.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Gets or sets the migration journal table name.</summary>
    public string MigrationTableName { get; set; } = "__IncursaSchemaMigrations";

    /// <summary>Gets or sets the migration id expected by integrity checks.</summary>
    public string? ExpectedMigrationId { get; set; }

    /// <summary>Gets or sets whether the provider applies its schema at host startup.</summary>
    public bool EnableSchemaDeployment { get; set; } = true;

    /// <summary>Gets or sets whether scheduling contracts are registered.</summary>
    public bool EnableScheduling { get; set; } = true;

    /// <summary>Gets or sets whether inbox contracts are registered.</summary>
    public bool EnableInbox { get; set; } = true;

    /// <summary>Gets or sets whether outbox contracts are registered.</summary>
    public bool EnableOutbox { get; set; } = true;

    /// <summary>Gets or sets whether lease contracts are registered.</summary>
    public bool EnableLeases { get; set; } = true;

    /// <summary>Gets or sets whether idempotency contracts are registered.</summary>
    public bool EnableIdempotency { get; set; } = true;

    /// <summary>Gets or sets whether fanout repositories are registered.</summary>
    public bool EnableFanout { get; set; } = true;

    /// <summary>Gets or sets whether operation tracking contracts are registered.</summary>
    public bool EnableOperations { get; set; } = true;

    /// <summary>Gets or sets whether audit contracts are registered.</summary>
    public bool EnableAudit { get; set; } = true;

    /// <summary>Gets or sets whether the SQLite health check is registered.</summary>
    public bool EnableHealthChecks { get; set; } = true;

    /// <summary>Gets or sets whether low-resource maintenance workers are registered.</summary>
    public bool EnableWorkers { get; set; } = true;

    /// <summary>Gets or sets the interval between maintenance passes.</summary>
    public TimeSpan WorkerInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets dispatch worker bounds and retry timing.</summary>
    public SqliteWorkerOptions Workers { get; set; } = new();

    /// <summary>Gets or sets the inbox-specific table and retention settings.</summary>
    public SqliteInboxOptions Inbox { get; set; } = new();

    /// <summary>Gets or sets lease-specific settings.</summary>
    public SqliteLeaseOptions Leases { get; set; } = new();

    /// <summary>Gets or sets idempotency settings.</summary>
    public SqliteIdempotencyOptions Idempotency { get; set; } = new();

    /// <summary>Gets or sets operation tracking settings.</summary>
    public SqliteOperationOptions Operations { get; set; } = new();

    /// <summary>Gets or sets audit settings.</summary>
    public SqliteAuditOptions Audit { get; set; } = new();

    internal void Validate()
    {
        SqlitePath.ValidateLocalFilePath(DataSource, nameof(DataSource));

        if (BusyTimeoutMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BusyTimeoutMilliseconds));
        }

        if (MaxRetryCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryCount));
        }

        if (RetryBaseDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryBaseDelay));
        }

        SqliteIdentifier.Validate(MigrationTableName, nameof(MigrationTableName));

        if (ExpectedMigrationId is not null && string.IsNullOrWhiteSpace(ExpectedMigrationId))
        {
            throw new ArgumentException("The expected migration id cannot be empty.", nameof(ExpectedMigrationId));
        }

        if (WorkerInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(WorkerInterval));
        }

        ArgumentNullException.ThrowIfNull(Inbox);
        ArgumentNullException.ThrowIfNull(Leases);
        ArgumentNullException.ThrowIfNull(Idempotency);
        ArgumentNullException.ThrowIfNull(Operations);
        ArgumentNullException.ThrowIfNull(Audit);
        ArgumentNullException.ThrowIfNull(Workers);
        Inbox.Validate();
        Leases.Validate();
        Idempotency.Validate();
        Operations.Validate();
        Audit.Validate();
        Workers.Validate();
    }
}
