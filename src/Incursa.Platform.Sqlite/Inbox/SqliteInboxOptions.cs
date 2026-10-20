// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>Options for the single-host SQLite inbox.</summary>
public sealed class SqliteInboxOptions
{
    /// <summary>Gets or sets the inbox table name.</summary>
    public string TableName { get; set; } = "Inbox";

    /// <summary>Gets or sets the maximum number of attempts before application code may dead-letter a message.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Gets or sets the retention period for completed messages.</summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Gets or sets the interval used by an optional cleanup worker.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Gets or sets a value indicating whether the caller should deploy the inbox schema.</summary>
    public bool EnableSchemaDeployment { get; set; } = true;

    /// <summary>Validates the option values.</summary>
    public void Validate()
    {
        SqliteIdentifier.Validate(TableName, nameof(TableName));
        if (MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        }

        if (RetentionPeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetentionPeriod));
        }

        if (CleanupInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CleanupInterval));
        }
    }
}
