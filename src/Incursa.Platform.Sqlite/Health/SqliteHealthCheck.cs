// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Incursa.Platform;

/// <summary>Checks local SQLite connectivity and on-disk integrity.</summary>
public sealed class SqliteHealthCheck : IHealthCheck
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteIntegrityChecker integrityChecker;

    /// <summary>Initializes the health check.</summary>
    public SqliteHealthCheck(SqliteConnectionFactory connectionFactory, SqliteIntegrityChecker integrityChecker)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.integrityChecker = integrityChecker ?? throw new ArgumentNullException(nameof(integrityChecker));
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(connectionFactory.FullDataSourcePath))
            {
                return HealthCheckResult.Unhealthy("The configured SQLite database file does not exist.");
            }

            var result = await integrityChecker.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (!result.IsHealthy)
            {
                return HealthCheckResult.Unhealthy(
                    $"SQLite integrity check failed: result={result.IntegrityResult}, foreignKeyViolations={result.ForeignKeyViolationCount}.");
            }

            return HealthCheckResult.Healthy("SQLite WAL database is reachable and passes integrity checks.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQLite health check failed.", ex);
        }
    }
}
