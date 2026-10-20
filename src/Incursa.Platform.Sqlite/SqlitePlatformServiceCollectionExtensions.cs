// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Incursa.Platform.Audit;
using Incursa.Platform.Idempotency;
using Incursa.Platform.Operations;
using Incursa.Platform.Outbox;

namespace Incursa.Platform;

/// <summary>DI registration for the single-host SQLite platform provider.</summary>
public static class SqlitePlatformServiceCollectionExtensions
{
    /// <summary>Registers SQLite services for one local host and one database file.</summary>
    public static IServiceCollection AddSqlitePlatform(this IServiceCollection services, string dataSource, Action<SqlitePlatformOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            throw new ArgumentException("A local SQLite data source is required.", nameof(dataSource));
        }

        var options = new SqlitePlatformOptions { DataSource = dataSource };
        configure?.Invoke(options);
        options.Validate();
        return services.AddSqlitePlatform(options);
    }

    /// <summary>Registers SQLite services for one local host and one database file.</summary>
    public static IServiceCollection AddSqlitePlatform(this IServiceCollection services, SqlitePlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        services.TryAddSingleton(options.Inbox);
        services.TryAddSingleton(options.Leases);
        services.TryAddSingleton(options.Idempotency);
        services.TryAddSingleton(options.Operations);
        services.TryAddSingleton(options.Audit);
        services.TryAddSingleton(options.Workers);
        services.TryAddSingleton<TimeProvider>(_ => TimeProvider.System);
        services.TryAddSingleton<SqliteConnectionFactory>();
        services.TryAddSingleton<SqliteRetryPolicy>();
        services.TryAddSingleton<SqliteMigrationRunner>();
        services.TryAddSingleton<SqliteSchemaMigrator>();
        services.TryAddSingleton<SqliteIntegrityChecker>();
        services.TryAddSingleton<SqliteBackupService>();

        if (options.EnableScheduling)
        {
            services.TryAddSingleton<SqliteSchedulerClient>();
            services.TryAddSingleton<ISchedulerClient>(sp => sp.GetRequiredService<SqliteSchedulerClient>());
            services.TryAddSingleton<SqliteSchedulerStore>();
            services.TryAddSingleton<ISchedulerStore>(sp => sp.GetRequiredService<SqliteSchedulerStore>());
            services.TryAddSingleton<SqliteGlobalSchedulerClient>();
            services.TryAddSingleton<IGlobalSchedulerClient>(sp => sp.GetRequiredService<SqliteGlobalSchedulerClient>());
            services.TryAddSingleton<SqliteGlobalSchedulerStore>();
            services.TryAddSingleton<IGlobalSchedulerStore>(sp => sp.GetRequiredService<SqliteGlobalSchedulerStore>());
            services.TryAddSingleton<SqliteSchedulerStoreProvider>();
            services.TryAddSingleton<ISchedulerStoreProvider>(sp => sp.GetRequiredService<SqliteSchedulerStoreProvider>());
        }

        if (options.EnableLeases)
        {
            services.TryAddSingleton<SqliteLeaseFactory>();
            services.TryAddSingleton<ISystemLeaseFactory>(sp => sp.GetRequiredService<SqliteLeaseFactory>());
            services.TryAddSingleton<SqliteLeaseFactoryProvider>();
            services.TryAddSingleton<ILeaseFactoryProvider>(sp => sp.GetRequiredService<SqliteLeaseFactoryProvider>());
        }

        if (options.EnableIdempotency)
        {
            services.TryAddSingleton<SqliteIdempotencyStore>();
            services.TryAddSingleton<IIdempotencyStore>(sp => sp.GetRequiredService<SqliteIdempotencyStore>());
            services.TryAddSingleton<IIdempotencyCleanupStore>(sp => sp.GetRequiredService<SqliteIdempotencyStore>());
            services.TryAddSingleton<SqliteIdempotencyStoreProvider>();
            services.TryAddSingleton<IIdempotencyStoreProvider>(sp => sp.GetRequiredService<SqliteIdempotencyStoreProvider>());
        }

        if (options.EnableInbox)
        {
            services.TryAddSingleton<SqliteInboxWorkStore>();
            services.TryAddSingleton<IInboxWorkStore>(sp => sp.GetRequiredService<SqliteInboxWorkStore>());
            services.TryAddSingleton<SqliteInboxService>();
            services.TryAddSingleton<IInbox>(sp => sp.GetRequiredService<SqliteInboxService>());
        }

        if (options.EnableOutbox)
        {
            services.TryAddSingleton<SqliteOutboxStore>();
            services.TryAddSingleton<IOutboxStore>(sp => sp.GetRequiredService<SqliteOutboxStore>());
            services.TryAddSingleton<SqliteOutboxJoinStore>();
            services.TryAddSingleton<IOutboxJoinStore>(sp => sp.GetRequiredService<SqliteOutboxJoinStore>());
            services.TryAddSingleton<SqliteOutboxService>();
            services.TryAddSingleton<IOutbox>(sp => sp.GetRequiredService<SqliteOutboxService>());
            services.TryAddSingleton<SqliteOutboxStoreProvider>();
            services.TryAddSingleton<IOutboxStoreProvider>(sp => sp.GetRequiredService<SqliteOutboxStoreProvider>());
        }

        if (options.EnableFanout)
        {
            services.TryAddSingleton<SqliteFanoutRepositoryProvider>();
            services.TryAddSingleton<SqliteFanoutPolicyRepository>();
            services.TryAddSingleton<IFanoutPolicyRepository>(sp => sp.GetRequiredService<SqliteFanoutPolicyRepository>());
            services.TryAddSingleton<SqliteFanoutCursorRepository>();
            services.TryAddSingleton<IFanoutCursorRepository>(sp => sp.GetRequiredService<SqliteFanoutCursorRepository>());
        }

        if (options.EnableOperations)
        {
            services.TryAddSingleton<SqliteOperationTracker>();
            services.TryAddSingleton<IOperationTracker>(sp => sp.GetRequiredService<SqliteOperationTracker>());
            services.TryAddSingleton<SqliteOperationWatcher>();
            services.TryAddSingleton<IOperationWatcher>(sp => sp.GetRequiredService<SqliteOperationWatcher>());
        }

        if (options.EnableAudit)
        {
            services.TryAddSingleton<SqliteAuditEventWriter>();
            services.TryAddSingleton<IAuditEventWriter>(sp => sp.GetRequiredService<SqliteAuditEventWriter>());
            services.TryAddSingleton<SqliteAuditEventReader>();
            services.TryAddSingleton<IAuditEventReader>(sp => sp.GetRequiredService<SqliteAuditEventReader>());
        }

        if (options.EnableSchemaDeployment)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SqliteSchemaDeploymentHostedService>());
        }

        if (options.EnableWorkers)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SqliteIntegrityMaintenanceHostedService>());

            if (options.EnableInbox)
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SqliteInboxDispatchHostedService>());
            }

            if (options.EnableOutbox)
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SqliteOutboxDispatchHostedService>());
            }

            if (options.EnableScheduling && options.EnableOutbox && options.EnableLeases)
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SqliteSchedulerDispatchHostedService>());
            }
        }

        if (options.EnableHealthChecks)
        {
            services.TryAddSingleton<SqliteHealthCheck>();
            services.AddHealthChecks().AddCheck<SqliteHealthCheck>("sqlite", failureStatus: HealthStatus.Unhealthy, tags: ["database", "sqlite"]);
        }

        return services;
    }
}
