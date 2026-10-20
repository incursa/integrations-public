// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform.Tests.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Incursa.Platform.Sqlite.Tests.TestUtilities;

internal sealed class SqliteSchedulerBehaviorHarness : ISchedulerBehaviorHarness
{
    private readonly SqlitePlatformBehaviorHarnessContext context = new("scheduler");
    private ISystemLeaseFactory? leaseFactory;

    public ISchedulerClient SchedulerClient => context.Provider.GetRequiredService<ISchedulerClient>();

    public ISchedulerStore SchedulerStore => context.Provider.GetRequiredService<ISchedulerStore>();

    public ISystemLeaseFactory LeaseFactory => leaseFactory ?? throw new InvalidOperationException("The SQLite behavior harness has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        await ResetAsync().ConfigureAwait(false);
    }

    public async Task ResetAsync()
    {
        await context.ResetAsync().ConfigureAwait(false);
        leaseFactory = new SqliteConformanceLeaseFactory(context.Provider.GetRequiredService<ISystemLeaseFactory>());
    }

    public ValueTask DisposeAsync() => context.DisposeAsync();
}
