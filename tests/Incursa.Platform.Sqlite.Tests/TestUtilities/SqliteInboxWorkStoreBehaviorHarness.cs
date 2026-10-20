// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform.Tests.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Incursa.Platform.Sqlite.Tests.TestUtilities;

internal sealed class SqliteInboxWorkStoreBehaviorHarness : IInboxWorkStoreBehaviorHarness
{
    private readonly SqlitePlatformBehaviorHarnessContext context = new("inbox");

    public IInbox Inbox => context.Provider.GetRequiredService<IInbox>();

    public IInboxWorkStore WorkStore => context.Provider.GetRequiredService<IInboxWorkStore>();

    public ValueTask InitializeAsync() => new(context.ResetAsync());

    public Task ResetAsync() => context.ResetAsync();

    public ValueTask DisposeAsync() => context.DisposeAsync();
}
