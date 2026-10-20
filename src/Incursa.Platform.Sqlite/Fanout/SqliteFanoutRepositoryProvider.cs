// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>
/// Provides the one policy and cursor repository owned by a local SQLite file.
/// SQLite intentionally has no dynamic or multi-host fanout discovery surface.
/// </summary>
internal sealed class SqliteFanoutRepositoryProvider
{
    private readonly IReadOnlyList<IFanoutPolicyRepository> policyRepositories;
    private readonly IReadOnlyList<IFanoutCursorRepository> cursorRepositories;
    private readonly Dictionary<IFanoutPolicyRepository, string> policyIdentifiers;
    private readonly Dictionary<IFanoutCursorRepository, string> cursorIdentifiers;
    private readonly Dictionary<string, IFanoutPolicyRepository> policyByKey;
    private readonly Dictionary<string, IFanoutCursorRepository> cursorByKey;

    /// <summary>Initializes a provider for one configured SQLite database.</summary>
    public SqliteFanoutRepositoryProvider(
        SqliteConnectionFactory connectionFactory,
        SqliteRetryPolicy retryPolicy,
        TimeProvider? timeProvider = null,
        string key = "default")
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var policy = new SqliteFanoutPolicyRepository(connectionFactory, retryPolicy, timeProvider);
        var cursor = new SqliteFanoutCursorRepository(connectionFactory, retryPolicy, timeProvider);
        policyRepositories = [policy];
        cursorRepositories = [cursor];
        policyIdentifiers = new Dictionary<IFanoutPolicyRepository, string> { [policy] = key };
        cursorIdentifiers = new Dictionary<IFanoutCursorRepository, string> { [cursor] = key };
        policyByKey = new Dictionary<string, IFanoutPolicyRepository>(StringComparer.Ordinal) { [key] = policy };
        cursorByKey = new Dictionary<string, IFanoutCursorRepository>(StringComparer.Ordinal) { [key] = cursor };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IFanoutPolicyRepository>> GetAllPolicyRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(policyRepositories);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IFanoutCursorRepository>> GetAllCursorRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(cursorRepositories);
    }

    /// <inheritdoc />
    public string GetRepositoryIdentifier(IFanoutPolicyRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return policyIdentifiers.TryGetValue(repository, out var identifier) ? identifier : "unknown";
    }

    /// <inheritdoc />
    public string GetRepositoryIdentifier(IFanoutCursorRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return cursorIdentifiers.TryGetValue(repository, out var identifier) ? identifier : "unknown";
    }

    /// <inheritdoc />
    public IFanoutPolicyRepository? GetPolicyRepositoryByKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return policyByKey.TryGetValue(key, out var repository) ? repository : null;
    }

    /// <inheritdoc />
    public IFanoutCursorRepository? GetCursorRepositoryByKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return cursorByKey.TryGetValue(key, out var repository) ? repository : null;
    }
}
