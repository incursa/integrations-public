// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>Applies SQLite migrations atomically and records applied SQL hashes.</summary>
public sealed class SqliteMigrationRunner
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;
    private readonly string migrationTableName;

    /// <summary>Initializes a runner from a connection factory and migration options.</summary>
    public SqliteMigrationRunner(SqliteConnectionFactory connectionFactory, SqlitePlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.connectionFactory = connectionFactory;
        retryPolicy = new SqliteRetryPolicy(options);
        migrationTableName = SqliteIdentifier.Quote(options.MigrationTableName);
    }

    /// <summary>Applies pending migrations in the exact order supplied by the caller.</summary>
    public Task ApplyAsync(IEnumerable<SqliteMigration> migrations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        var ordered = migrations.ToArray();
        foreach (var migration in ordered)
        {
            migration.Validate();
        }

        if (ordered.Select(static migration => migration.Id).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
        {
            throw new ArgumentException("Migration ids must be unique in the supplied sequence.", nameof(migrations));
        }

        return retryPolicy.ExecuteAsync(ct => ApplyOnceAsync(ordered, ct), cancellationToken);
    }

    private async Task ApplyOnceAsync(IReadOnlyList<SqliteMigration> migrations, CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.OpenConnection();
        var transactionStarted = false;
        var appliedCount = 0;
        try
        {
            // BEGIN IMMEDIATE serializes migration runners across processes.
            await ExecuteAsync(connection, "BEGIN IMMEDIATE;", cancellationToken).ConfigureAwait(false);
            transactionStarted = true;
            await ExecuteAsync(
                connection,
                $"CREATE TABLE IF NOT EXISTS {migrationTableName} (sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, migration_id TEXT NOT NULL UNIQUE, sql_sha256 TEXT NOT NULL, applied_at_utc TEXT NOT NULL);",
                cancellationToken).ConfigureAwait(false);

            var applied = await ReadAppliedAsync(connection, cancellationToken).ConfigureAwait(false);
            foreach (var migration in migrations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = ComputeHash(migration.Sql);
                if (applied.TryGetValue(migration.Id, out var appliedHash))
                {
                    if (!string.Equals(appliedHash, hash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Migration '{migration.Id}' was already applied with different SQL (journal hash mismatch).");
                    }

                    continue;
                }

                await ExecuteAsync(connection, migration.Sql, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(
                    connection,
                    $"INSERT INTO {migrationTableName} (migration_id, sql_sha256, applied_at_utc) VALUES ($id, $hash, $at);",
                    cancellationToken,
                    ("$id", migration.Id),
                    ("$hash", hash),
                    ("$at", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture))).ConfigureAwait(false);
                appliedCount++;
            }

            await ExecuteAsync(connection, "COMMIT;", cancellationToken).ConfigureAwait(false);
            transactionStarted = false;
            SqlitePlatformMetrics.RecordMigrationsApplied(appliedCount);
        }
        catch
        {
            SqlitePlatformMetrics.RecordMigrationFailure();
            if (transactionStarted)
            {
                try
                {
                    await ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false);
                }
                catch (SqliteException)
                {
                    // Preserve the original migration failure.
                }
            }

            throw;
        }
    }

    private async Task<Dictionary<string, string>> ReadAppliedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT migration_id, sql_sha256 FROM {migrationTableName} ORDER BY sequence ASC;";
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0), reader.GetString(1));
        }

        return result;
    }

    private static string ComputeHash(string sql) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
