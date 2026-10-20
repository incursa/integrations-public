// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>Single-store scheduler provider for one local SQLite file.</summary>
public sealed class SqliteSchedulerStoreProvider : ISchedulerStoreProvider
{
    private readonly ISchedulerStore store;
    private readonly ISchedulerClient client;
    private readonly string identifier;

    /// <summary>Initializes a provider over the supplied scheduler objects.</summary>
    public SqliteSchedulerStoreProvider(ISchedulerStore store, ISchedulerClient client, string? identifier = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.identifier = string.IsNullOrWhiteSpace(identifier) ? "sqlite" : identifier;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ISchedulerStore>> GetAllStoresAsync() => Task.FromResult<IReadOnlyList<ISchedulerStore>>(new[] { store });

    /// <inheritdoc />
    public string GetStoreIdentifier(ISchedulerStore schedulerStore) => ReferenceEquals(store, schedulerStore) ? identifier : throw new ArgumentException("The scheduler store is not registered by this provider.", nameof(schedulerStore));

    /// <inheritdoc />
    public ISchedulerStore? GetStoreByKey(string key) => string.Equals(key, identifier, StringComparison.OrdinalIgnoreCase) || string.Equals(key, "sqlite", StringComparison.OrdinalIgnoreCase) ? store : null;

    /// <inheritdoc />
    public ISchedulerClient? GetSchedulerClientByKey(string key) => string.Equals(key, identifier, StringComparison.OrdinalIgnoreCase) || string.Equals(key, "sqlite", StringComparison.OrdinalIgnoreCase) ? client : null;

    /// <inheritdoc />
    public IOutbox? GetOutboxByKey(string key) => null;
}
