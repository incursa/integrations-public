// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform.Outbox;
using Incursa.Platform.Tests.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Incursa.Platform.Sqlite.Tests.TestUtilities;

internal sealed class SqliteOutboxStoreBehaviorHarness : IOutboxStoreBehaviorHarness
{
    private readonly SqlitePlatformBehaviorHarnessContext context = new("outbox");

    public IOutbox Outbox => context.Provider.GetRequiredService<IOutbox>();

    public IOutboxStore Store => context.Provider.GetRequiredService<IOutboxStore>();

    public ValueTask InitializeAsync() => new(context.ResetAsync());

    public Task ResetAsync() => context.ResetAsync();

    public ValueTask DisposeAsync() => context.DisposeAsync();
}
