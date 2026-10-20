// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>Retries SQLite operations only for transient busy or locked errors.</summary>
public sealed class SqliteRetryPolicy
{
    private readonly SqlitePlatformOptions options;

    /// <summary>Initializes a retry policy from provider options.</summary>
    public SqliteRetryPolicy(SqlitePlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
    }

    /// <summary>Executes an operation with bounded busy/locked retries.</summary>
    public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ExecuteAsync(
            async ct =>
            {
                await operation(ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Executes an operation with bounded busy/locked retries and returns its result.</summary>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsTransient(ex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt >= options.MaxRetryCount)
                {
                    SqlitePlatformMetrics.RecordRetryExhausted();
                    throw;
                }

                SqlitePlatformMetrics.RecordRetry();
                var delay = TimeSpan.FromMilliseconds(Math.Min(options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt), 5_000));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Returns whether an exception represents a retryable SQLite lock conflict.</summary>
    public static bool IsTransient(SqliteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        // SQLite extended result codes retain SQLITE_BUSY/SQLITE_LOCKED in the
        // low byte (for example SQLITE_BUSY_TIMEOUT and SQLITE_LOCKED_SHAREDCACHE).
        var primaryCode = exception.SqliteErrorCode & 0xFF;
        return primaryCode is 5 or 6;
    }
}
