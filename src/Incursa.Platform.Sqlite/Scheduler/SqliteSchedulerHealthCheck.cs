// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Incursa.Platform;

/// <summary>Readiness check for the local SQLite scheduler store.</summary>
public sealed class SqliteSchedulerHealthCheck : IHealthCheck
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;

    /// <summary>Initializes a scheduler health check.</summary>
    public SqliteSchedulerHealthCheck(SqliteConnectionFactory connectionFactory, SqliteRetryPolicy retryPolicy)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await SqliteSchedulerSchema.EnsureAsync(connectionFactory, retryPolicy, cancellationToken).ConfigureAwait(false);
            var result = await retryPolicy.ExecuteAsync(async ct =>
            {
                using var connection = connectionFactory.OpenExistingConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA integrity_check;";
                return Convert.ToString(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            }, cancellationToken).ConfigureAwait(false);
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)
                ? HealthCheckResult.Healthy("SQLite scheduler database is healthy.")
                : HealthCheckResult.Unhealthy($"SQLite integrity check returned '{result}'.");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("SQLite scheduler database is unavailable.", ex);
        }
    }
}
