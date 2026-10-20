// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>Single-store provider for a local SQLite database file.</summary>
internal sealed class SqliteOutboxStoreProvider : IOutboxStoreProvider
{
    private readonly IOutboxStore store;
    private readonly IOutbox outbox;
    private readonly string identifier;

    public SqliteOutboxStoreProvider(IOutboxStore store, IOutbox outbox, SqliteConnectionFactory factory)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        ArgumentNullException.ThrowIfNull(factory);
        identifier = factory.FullDataSourcePath;
    }

    public Task<IReadOnlyList<IOutboxStore>> GetAllStoresAsync() => Task.FromResult<IReadOnlyList<IOutboxStore>>([store]);

    public string GetStoreIdentifier(IOutboxStore value) => ReferenceEquals(value, store) ? identifier : "unknown";

    public IOutboxStore? GetStoreByKey(string key) => string.Equals(key, identifier, StringComparison.Ordinal) ? store : null;

    public IOutbox? GetOutboxByKey(string key) => string.Equals(key, identifier, StringComparison.Ordinal) ? outbox : null;
}
