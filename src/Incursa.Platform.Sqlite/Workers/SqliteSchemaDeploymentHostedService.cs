// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Incursa.Platform;

/// <summary>Applies the configured SQLite schema once during host startup.</summary>
public sealed class SqliteSchemaDeploymentHostedService : IHostedService
{
    private readonly SqliteSchemaMigrator migrator;
    private readonly SqlitePlatformOptions options;
    private readonly ILogger<SqliteSchemaDeploymentHostedService> logger;

    /// <summary>Initializes the schema deployment service.</summary>
    public SqliteSchemaDeploymentHostedService(
        SqliteSchemaMigrator migrator,
        SqlitePlatformOptions options,
        ILogger<SqliteSchemaDeploymentHostedService> logger)
    {
        this.migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.EnableSchemaDeployment)
        {
            logger.LogDebug("SQLite schema deployment is disabled.");
            return;
        }

        await migrator.ApplyAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("SQLite platform schema deployment completed.");
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
