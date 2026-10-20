// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>Options for the single-host SQLite lease store.</summary>
public sealed class SqliteLeaseOptions
{
    /// <summary>Gets or sets the lease table name.</summary>
    public string TableName { get; set; } = "SqliteLeases";

    /// <summary>Gets or sets the default duration used by callers that do not supply one.</summary>
    public TimeSpan DefaultLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the fraction of the duration at which automatic renewal runs.</summary>
    public double RenewPercent { get; set; } = 0.6d;

    /// <summary>Gets or sets whether the feature schema is created on first use.</summary>
    public bool EnableSchemaDeployment { get; set; } = true;

    /// <summary>Validates option values.</summary>
    public void Validate()
    {
        SqliteIdentifier.Validate(TableName, nameof(TableName));
        if (DefaultLeaseDuration <= TimeSpan.Zero || DefaultLeaseDuration == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultLeaseDuration));
        }

        if (RenewPercent <= 0d || RenewPercent >= 1d || double.IsNaN(RenewPercent))
        {
            throw new ArgumentOutOfRangeException(nameof(RenewPercent));
        }
    }
}
