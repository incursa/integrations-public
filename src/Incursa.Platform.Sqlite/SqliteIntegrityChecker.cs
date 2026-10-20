// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>Runs full SQLite integrity checks.</summary>
public sealed class SqliteIntegrityChecker
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;
    private readonly string migrationTableName;
    private readonly string migrationTableNameValue;
    private readonly string? expectedMigrationId;

    /// <summary>Initializes an integrity checker.</summary>
    public SqliteIntegrityChecker(SqliteConnectionFactory connectionFactory, SqlitePlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.connectionFactory = connectionFactory;
        retryPolicy = new SqliteRetryPolicy(options);
        migrationTableName = SqliteIdentifier.Quote(options.MigrationTableName);
        migrationTableNameValue = options.MigrationTableName;
        expectedMigrationId = options.ExpectedMigrationId;
    }

    /// <summary>Runs integrity_check and foreign_key_check.</summary>
    public Task<SqliteIntegrityCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
        retryPolicy.ExecuteAsync(async ct =>
        {
            if (!File.Exists(connectionFactory.FullDataSourcePath))
            {
                throw new FileNotFoundException("The SQLite database does not exist; integrity checks never create databases.", connectionFactory.FullDataSourcePath);
            }

            using var connection = connectionFactory.OpenExistingConnection();
            using var integrityCommand = connection.CreateCommand();
            integrityCommand.CommandText = "PRAGMA integrity_check;";
            var integrity = Convert.ToString(await integrityCommand.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

            using var foreignKeyCommand = connection.CreateCommand();
            foreignKeyCommand.CommandText = "PRAGMA foreign_key_check;";
            var count = 0;
            using var reader = await foreignKeyCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                count++;
            }

            string? appliedMigrationId = null;
            if (expectedMigrationId is not null)
            {
                using var tableCommand = connection.CreateCommand();
                tableCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
                tableCommand.Parameters.AddWithValue("$name", migrationTableNameValue);
                var tableExists = Convert.ToInt32(await tableCommand.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1;
                if (tableExists)
                {
                    using var migrationCommand = connection.CreateCommand();
                    migrationCommand.CommandText = $"SELECT migration_id FROM {migrationTableName} ORDER BY sequence DESC LIMIT 1;";
                    appliedMigrationId = Convert.ToString(await migrationCommand.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            var result = new SqliteIntegrityCheckResult(integrity, count, appliedMigrationId, expectedMigrationId);
            SqlitePlatformMetrics.RecordIntegrityCheck(result.IsHealthy);
            return result;
        }, cancellationToken);
}
