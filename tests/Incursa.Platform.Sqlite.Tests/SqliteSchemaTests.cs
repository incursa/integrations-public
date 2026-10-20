// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform.Sqlite.Tests;

[Trait("Category", "Unit")]
public sealed class SqliteSchemaTests
{
    [Fact]
    public async Task CompleteSchemaBundle_IsTransactionalAndIdempotent()
    {
        using var database = new TemporaryDatabase();
        var migrator = new SqliteSchemaMigrator(
            new SqliteMigrationRunner(new SqliteConnectionFactory(database.Options), database.Options),
            database.Options);

        await migrator.ApplyAsync(TestContext.Current.CancellationToken);
        await migrator.ApplyAsync(TestContext.Current.CancellationToken);

        using var connection = new SqliteConnectionFactory(database.Options).OpenConnection();
        foreach (var table in new[] { "SqliteProviderMetadata", "SqliteTimers", "SqliteJobs", "SqliteJobRuns", "Outbox", "Inbox", "SqliteLeases", "SqliteIdempotency", "Operations", "AuditEvents" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", table);
            Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
        }

        using var journal = connection.CreateCommand();
        journal.CommandText = "SELECT COUNT(*) FROM \"__IncursaSchemaMigrations\";";
        Convert.ToInt32(journal.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(migrator.GetMigrations().Count);
    }

    [Fact]
    public async Task MigrationDrift_IsRejectedByTheSharedJournal()
    {
        using var database = new TemporaryDatabase();
        var runner = new SqliteMigrationRunner(new SqliteConnectionFactory(database.Options), database.Options);
        await runner.ApplyAsync([new SqliteMigration("schema-1", "CREATE TABLE DriftSample (Id INTEGER PRIMARY KEY);")], TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(() => runner.ApplyAsync(
            [new SqliteMigration("schema-1", "CREATE TABLE DriftSample (Id INTEGER PRIMARY KEY, Name TEXT);")],
            TestContext.Current.CancellationToken));
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"incursa-schema-{Guid.NewGuid():N}.sqlite");
            Options = new SqlitePlatformOptions { DataSource = Path };
        }

        public string Path { get; }

        public SqlitePlatformOptions Options { get; }

        public void Dispose()
        {
            foreach (var path in new[] { Path, Path + "-wal", Path + "-shm" })
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
