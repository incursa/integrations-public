// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>Routes all SQLite lease requests to the one local database.</summary>
public sealed class SqliteLeaseFactoryProvider : ILeaseFactoryProvider
{
    private readonly ISystemLeaseFactory factory;
    private readonly string identifier;

    /// <summary>Initializes a provider for one local SQLite lease factory.</summary>
    public SqliteLeaseFactoryProvider(ISystemLeaseFactory factory, string? identifier = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.identifier = string.IsNullOrWhiteSpace(identifier) ? "sqlite" : identifier;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ISystemLeaseFactory>> GetAllFactoriesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ISystemLeaseFactory>>(new[] { factory });

    /// <inheritdoc />
    public string GetFactoryIdentifier(ISystemLeaseFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return ReferenceEquals(this.factory, factory) ? identifier : "unknown";
    }

    /// <inheritdoc />
    public Task<ISystemLeaseFactory?> GetFactoryByKeyAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult<ISystemLeaseFactory?>(string.IsNullOrWhiteSpace(key) ? null : factory);
}
