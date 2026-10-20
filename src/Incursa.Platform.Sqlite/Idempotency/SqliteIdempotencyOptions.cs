// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>Options for the single-host SQLite idempotency store.</summary>
public sealed class SqliteIdempotencyOptions
{
    /// <summary>Gets or sets the idempotency table name.</summary>
    public string TableName { get; set; } = "SqliteIdempotency";

    /// <summary>Gets or sets how long an in-progress reservation remains owned.</summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets an optional per-key lock duration provider.</summary>
    public Func<string, TimeSpan>? LockDurationProvider { get; set; }

    /// <summary>Gets or sets whether the feature schema is created on first use.</summary>
    public bool EnableSchemaDeployment { get; set; } = true;

    /// <summary>Validates option values.</summary>
    public void Validate()
    {
        SqliteIdentifier.Validate(TableName, nameof(TableName));
        if (LockDuration <= TimeSpan.Zero && LockDuration != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(LockDuration));
        }
    }
}
