// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Incursa.Platform;

/// <summary>Runs bounded, low-frequency integrity checks for a local SQLite file.</summary>
public sealed class SqliteIntegrityMaintenanceHostedService : BackgroundService
{
    private readonly SqliteIntegrityChecker checker;
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqlitePlatformOptions options;
    private readonly ILogger<SqliteIntegrityMaintenanceHostedService> logger;

    /// <summary>Initializes the maintenance worker.</summary>
    public SqliteIntegrityMaintenanceHostedService(
        SqliteIntegrityChecker checker,
        SqliteConnectionFactory connectionFactory,
        SqlitePlatformOptions options,
        ILogger<SqliteIntegrityMaintenanceHostedService> logger)
    {
        this.checker = checker ?? throw new ArgumentNullException(nameof(checker));
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.WorkerInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!File.Exists(connectionFactory.FullDataSourcePath))
            {
                continue;
            }

            try
            {
                var result = await checker.CheckAsync(stoppingToken).ConfigureAwait(false);
                if (!result.IsHealthy)
                {
                    logger.LogError(
                        "SQLite integrity check reported an unhealthy database: {IntegrityResult}, foreign-key violations={ForeignKeyViolationCount}.",
                        result.IntegrityResult,
                        result.ForeignKeyViolationCount);
                }

                SqlitePlatformMetrics.RecordWorkerPass("integrity", result.IsHealthy);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                SqlitePlatformMetrics.RecordWorkerPass("integrity", false);
                logger.LogWarning(ex, "SQLite integrity maintenance check failed.");
            }
        }
    }
}
