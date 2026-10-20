// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>Deploys the SQLite provider schema using the transactional migration journal.</summary>
public sealed class SqliteSchemaMigrator
{
    private readonly SqliteMigrationRunner runner;
    private readonly SqlitePlatformOptions options;

    /// <summary>Initializes a schema migrator.</summary>
    public SqliteSchemaMigrator(SqliteMigrationRunner runner, SqlitePlatformOptions options)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
    }

    /// <summary>Gets the migrations selected by the current capability options.</summary>
    public IReadOnlyList<SqliteMigration> GetMigrations() => SqliteSchemaMigrations.GetMigrations(options);

    /// <summary>Applies all enabled migrations in one serialized transaction.</summary>
    public Task ApplyAsync(CancellationToken cancellationToken = default) =>
        SqliteSchemaMigrations.ApplyAsync(runner, options, cancellationToken);
}
