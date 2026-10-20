// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Incursa.Platform.Sqlite.Tests.TestUtilities;

internal sealed class SqlitePlatformBehaviorHarnessContext : IAsyncDisposable
{
    private readonly string directoryPath;
    private ServiceProvider? provider;

    public SqlitePlatformBehaviorHarnessContext(string name)
    {
        directoryPath = Path.Combine(Path.GetTempPath(), $"incursa-sqlite-conformance-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
    }

    public ServiceProvider Provider => provider ?? throw new InvalidOperationException("The SQLite behavior harness has not been initialized.");

    public async Task ResetAsync()
    {
        if (provider is not null)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            provider = null;
        }

        DeleteDatabaseFiles();

        var services = new ServiceCollection();
        services.AddSqlitePlatform(DatabasePath, options =>
        {
            options.EnableSchemaDeployment = false;
            options.EnableWorkers = false;
            options.BusyTimeoutMilliseconds = 2_000;
            options.MaxRetryCount = 10;
            options.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
        });

        provider = services.BuildServiceProvider();
        await Provider.GetRequiredService<SqliteSchemaMigrator>().ApplyAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (provider is not null)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            provider = null;
        }

        DeleteDatabaseFiles();
        try
        {
            Directory.Delete(directoryPath, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    private string DatabasePath => Path.Combine(directoryPath, "database.sqlite");

    private void DeleteDatabaseFiles()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(DatabasePath + suffix);
            }
            catch (IOException)
            {
            }
        }
    }
}
