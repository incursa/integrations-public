// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform.Idempotency;

namespace Incursa.Platform;

/// <summary>Routes all SQLite idempotency requests to the one local database.</summary>
public sealed class SqliteIdempotencyStoreProvider : IIdempotencyStoreProvider
{
    private readonly IIdempotencyStore store;
    private readonly string identifier;

    /// <summary>Initializes a provider for one local SQLite store.</summary>
    public SqliteIdempotencyStoreProvider(IIdempotencyStore store, string? identifier = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.identifier = string.IsNullOrWhiteSpace(identifier) ? "sqlite" : identifier;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IIdempotencyStore>> GetAllStoresAsync() =>
        Task.FromResult<IReadOnlyList<IIdempotencyStore>>(new[] { store });

    /// <inheritdoc />
    public string GetStoreIdentifier(IIdempotencyStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return ReferenceEquals(this.store, store) ? identifier : "unknown";
    }

    /// <inheritdoc />
    public IIdempotencyStore? GetStoreByKey(string key) => string.IsNullOrWhiteSpace(key) ? null : store;
}
