// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Incursa.Platform.Sqlite.Tests;

[Trait("Category", "Unit")]
public sealed class SqliteRegistrationTests
{
    [Fact]
    public void AddSqlitePlatform_RegistersSingleHostContractsAndInfrastructure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"incursa-registration-{Guid.NewGuid():N}.sqlite");
        try
        {
            var services = new ServiceCollection();
            services.AddSqlitePlatform(path);
            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<SqliteConnectionFactory>().DataSource.ShouldBe(path);
            provider.GetRequiredService<SqliteSchemaMigrator>().ShouldNotBeNull();
            provider.GetRequiredService<ISchedulerClient>().ShouldBeOfType<SqliteSchedulerClient>();
            provider.GetRequiredService<ISchedulerStore>().ShouldBeOfType<SqliteSchedulerStore>();
            provider.GetRequiredService<IInbox>().ShouldNotBeNull();
            provider.GetRequiredService<IOutbox>().ShouldNotBeNull();
            provider.GetRequiredService<Incursa.Platform.Idempotency.IIdempotencyStore>().ShouldNotBeNull();
            provider.GetRequiredService<Incursa.Platform.Operations.IOperationTracker>().ShouldNotBeNull();
            provider.GetRequiredService<Incursa.Platform.Audit.IAuditEventWriter>().ShouldNotBeNull();
            provider.GetRequiredService<SqliteHealthCheck>().ShouldNotBeNull();
        }
        finally
        {
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }
    }

    [Fact]
    public void AddSqlitePlatform_CanDisableCapabilities()
    {
        var path = Path.Combine(Path.GetTempPath(), $"incursa-registration-{Guid.NewGuid():N}.sqlite");
        try
        {
            var services = new ServiceCollection();
            services.AddSqlitePlatform(path, options =>
            {
                options.EnableScheduling = false;
                options.EnableInbox = false;
                options.EnableOutbox = false;
                options.EnableIdempotency = false;
                options.EnableOperations = false;
                options.EnableAudit = false;
                options.EnableLeases = false;
                options.EnableFanout = false;
                options.EnableHealthChecks = false;
                options.EnableWorkers = false;
            });
            using var provider = services.BuildServiceProvider();
            Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<ISchedulerClient>());
            provider.GetRequiredService<SqliteSchemaMigrator>().GetMigrations().Count.ShouldBe(1);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
