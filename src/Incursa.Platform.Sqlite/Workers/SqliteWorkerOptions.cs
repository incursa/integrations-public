// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>Bounds and timing controls for the local SQLite dispatch workers.</summary>
public sealed class SqliteWorkerOptions
{
    /// <summary>Gets or sets the delay between polling passes.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the maximum number of items claimed in one pass.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Gets or sets the work-item lease duration.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the delay before a transient failure is retried.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the maximum number of attempts before a work item is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 5;

    internal void Validate()
    {
        if (PollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollingInterval));
        }

        if (BatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchSize));
        }

        if (LeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
        }

        if (RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelay));
        }

        if (MaxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        }
    }
}
