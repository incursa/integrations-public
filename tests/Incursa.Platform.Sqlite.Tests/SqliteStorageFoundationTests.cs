// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Incursa.Platform.Sqlite.Tests;

[Trait("Category", "Unit")]
public sealed class SqliteStorageFoundationTests
{
    [Theory]
    [InlineData(":memory:")]
    [InlineData("file:database.sqlite")]
    [InlineData("database.sqlite")]
    [InlineData("C:database.sqlite")]
    [InlineData(@"\\server\share\database.sqlite")]
    public void ConnectionFactory_RejectsNonRootedLocalDataSources(string dataSource)
    {
        var options = new SqlitePlatformOptions { DataSource = dataSource };

        Should.Throw<ArgumentException>(() => new SqliteConnectionFactory(options));
    }

    [Fact]
    public void OpenConnection_EnforcesWalFullForeignKeysAndBusyTimeout()
    {
        using var fixture = new TemporaryDatabase();
        fixture.Options.BusyTimeoutMilliseconds = 1_234;
        var factory = new SqliteConnectionFactory(fixture.Options);
        using var connection = factory.OpenConnection();

        Convert.ToString(Scalar(connection, "PRAGMA journal_mode;"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe("wal", StringCompareShould.IgnoreCase);
        Convert.ToInt32(Scalar(connection, "PRAGMA synchronous;"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(2);
        Convert.ToInt32(Scalar(connection, "PRAGMA foreign_keys;"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
        Convert.ToInt32(Scalar(connection, "PRAGMA busy_timeout;"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1_234);
    }

    [Fact]
    public async Task Migrations_RollBackAllChangesAndCanBeAppliedAgain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TemporaryDatabase();
        var runner = CreateRunner(fixture);
        var failing = new[]
        {
            new SqliteMigration("001", "CREATE TABLE sample (id INTEGER PRIMARY KEY);"),
            new SqliteMigration("002", "INSERT INTO missing_table VALUES (1);"),
        };

        await Should.ThrowAsync<SqliteException>(() => runner.ApplyAsync(failing, cancellationToken));

        using (var connection = new SqliteConnectionFactory(fixture.Options).OpenConnection())
        {
            Scalar(connection, "SELECT 1 FROM sqlite_master WHERE name='sample';").ShouldBeNull();
            Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name='__IncursaSchemaMigrations';").ShouldBe(0);
        }

        await runner.ApplyAsync(
        [
            new SqliteMigration("001", "CREATE TABLE sample (id INTEGER PRIMARY KEY);"),
            new SqliteMigration("002", "INSERT INTO sample (id) VALUES (1);"),
        ], cancellationToken);

        using var verified = new SqliteConnectionFactory(fixture.Options).OpenConnection();
        Convert.ToInt32(Scalar(verified, "SELECT COUNT(*) FROM sample;"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
        Convert.ToInt32(Scalar(verified, "SELECT COUNT(*) FROM \"__IncursaSchemaMigrations\";"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(2);
    }

    [Fact]
    public async Task Migrations_AreIdempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TemporaryDatabase();
        var runner = CreateRunner(fixture);
        var migrations = new[]
        {
            new SqliteMigration("001", "CREATE TABLE sample (id INTEGER PRIMARY KEY); INSERT INTO sample VALUES (1);"),
        };

        await runner.ApplyAsync(migrations, cancellationToken);
        await runner.ApplyAsync(migrations, cancellationToken);

        using var connection = new SqliteConnectionFactory(fixture.Options).OpenConnection();
        Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM sample;"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
        Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM \"__IncursaSchemaMigrations\";"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
    }

    [Fact]
    public async Task Migrations_RejectDuplicateAndInvalidIds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TemporaryDatabase();
        var runner = CreateRunner(fixture);

        await Should.ThrowAsync<ArgumentException>(() => runner.ApplyAsync(
        [
            new SqliteMigration("001", "CREATE TABLE one (id INTEGER PRIMARY KEY);"),
            new SqliteMigration("001", "CREATE TABLE two (id INTEGER PRIMARY KEY);"),
        ], cancellationToken));

        await Should.ThrowAsync<ArgumentException>(() => runner.ApplyAsync(
        [new SqliteMigration("  ", "CREATE TABLE invalid_id (id INTEGER PRIMARY KEY);")], cancellationToken));
    }

    [Theory]
    [InlineData("COMMIT;")]
    [InlineData("CREATE TABLE sample (id INTEGER PRIMARY KEY); ROLLBACK;")]
    [InlineData("VACUUM;")]
    public async Task Migrations_RejectSqlThatControlsTheTransaction(string sql)
    {
        using var fixture = new TemporaryDatabase();

        await Should.ThrowAsync<ArgumentException>(() => CreateRunner(fixture).ApplyAsync(
        [new SqliteMigration("001", sql)], TestContext.Current.CancellationToken));

        File.Exists(fixture.Options.DataSource).ShouldBeFalse();
    }

    [Fact]
    public async Task Migrations_AllowTransactionWordsInsideStringLiterals()
    {
        using var fixture = new TemporaryDatabase();
        var runner = CreateRunner(fixture);

        await runner.ApplyAsync(
        [new SqliteMigration("001", "CREATE TABLE sample (value TEXT); INSERT INTO sample VALUES ('COMMIT; ROLLBACK; VACUUM;');")],
        TestContext.Current.CancellationToken);

        using var connection = new SqliteConnectionFactory(fixture.Options).OpenConnection();
        Convert.ToString(Scalar(connection, "SELECT value FROM sample;"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe("COMMIT; ROLLBACK; VACUUM;");
    }

    [Fact]
    public async Task Migrations_AllowCreateTriggerWithCompoundBody()
    {
        using var fixture = new TemporaryDatabase();
        var runner = CreateRunner(fixture);

        await runner.ApplyAsync(
        [
            new SqliteMigration(
                "001",
                """
                CREATE TABLE sample (id INTEGER PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE sample_audit (sample_id INTEGER NOT NULL, value TEXT NOT NULL);
                CREATE TRIGGER sample_after_insert
                AFTER INSERT ON sample
                BEGIN
                    INSERT INTO sample_audit (sample_id, value) VALUES (NEW.id, NEW.value);
                END;
                """),
        ], TestContext.Current.CancellationToken);

        using var connection = new SqliteConnectionFactory(fixture.Options).OpenConnection();
        Execute(connection, "INSERT INTO sample (value) VALUES ('triggered');");
        Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM sample_audit;"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
        Convert.ToString(Scalar(connection, "SELECT value FROM sample_audit;"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe("triggered");
    }

    [Fact]
    public async Task HealthCheck_UsesNormalizedFullDataSourcePathForWhitespaceDataSource()
    {
        using var fixture = new TemporaryDatabase();
        var options = new SqlitePlatformOptions { DataSource = $"  {fixture.Options.DataSource}  " };
        var factory = new SqliteConnectionFactory(options);
        using (factory.OpenConnection())
        {
        }

        factory.FullDataSourcePath.ShouldBe(Path.GetFullPath(fixture.Options.DataSource));
        var health = await new SqliteHealthCheck(factory, new SqliteIntegrityChecker(factory, options))
            .CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext(), TestContext.Current.CancellationToken);

        health.Status.ShouldBe(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);
    }

    [Fact]
    public async Task Migrations_RejectHashDriftForAnAppliedId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TemporaryDatabase();
        var runner = CreateRunner(fixture);

        await runner.ApplyAsync(
        [new SqliteMigration("001", "CREATE TABLE sample (id INTEGER PRIMARY KEY);")], cancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(() => runner.ApplyAsync(
        [new SqliteMigration("001", "CREATE TABLE changed (id INTEGER PRIMARY KEY);")], cancellationToken));

        using var connection = new SqliteConnectionFactory(fixture.Options).OpenConnection();
        Scalar(connection, "SELECT 1 FROM sqlite_master WHERE name='changed';").ShouldBeNull();
    }

    [Fact]
    public async Task Migrations_ConcurrentRunnersApplyEachMigrationOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TemporaryDatabase();
        fixture.Options.BusyTimeoutMilliseconds = 250;
        fixture.Options.MaxRetryCount = 20;
        fixture.Options.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
        var migrations = new[]
        {
            new SqliteMigration("001", "CREATE TABLE sample (id INTEGER PRIMARY KEY);"),
            new SqliteMigration("002", "INSERT INTO sample VALUES (1);"),
        };

        var first = Task.Run(() => CreateRunner(fixture).ApplyAsync(migrations, cancellationToken), cancellationToken);
        var second = Task.Run(() => CreateRunner(fixture).ApplyAsync(migrations, cancellationToken), cancellationToken);
        await Task.WhenAll(first, second);

        using var connection = new SqliteConnectionFactory(fixture.Options).OpenConnection();
        Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM sample;"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
        Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM \"__IncursaSchemaMigrations\";"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(2);
    }

    [Fact]
    public async Task RetryPolicy_RetriesBusyErrorsWithinConfiguredBound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TemporaryDatabase();
        fixture.Options.RetryBaseDelay = TimeSpan.Zero;
        fixture.Options.MaxRetryCount = 2;
        var policy = new SqliteRetryPolicy(fixture.Options);
        var attempts = 0;

        var result = await policy.ExecuteAsync<int>(_ =>
        {
            attempts++;
            return attempts < 3
                ? Task.FromException<int>(new SqliteException("busy", 5))
                : Task.FromResult(42);
        }, cancellationToken);

        result.ShouldBe(42);
        attempts.ShouldBe(3);

        attempts = 0;
        await Should.ThrowAsync<SqliteException>(() => policy.ExecuteAsync<int>(_ =>
        {
            attempts++;
            return Task.FromException<int>(new SqliteException("not transient", 1));
        }, cancellationToken));
        attempts.ShouldBe(1);
        SqliteRetryPolicy.IsTransient(new SqliteException("extended busy", 5 | (2 << 8))).ShouldBeTrue();
    }

    [Fact]
    public async Task RetryPolicy_HonorsCancellationWhileWaitingToRetry()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var fixture = new TemporaryDatabase();
        fixture.Options.RetryBaseDelay = TimeSpan.FromSeconds(30);
        fixture.Options.MaxRetryCount = 10;
        var policy = new SqliteRetryPolicy(fixture.Options);
        var attempts = 0;

        await Should.ThrowAsync<OperationCanceledException>(() => policy.ExecuteAsync<int>(_ =>
        {
            attempts++;
            cancellation.Cancel();
            return Task.FromException<int>(new SqliteException("busy", 5));
        }, cancellation.Token));

        attempts.ShouldBe(1);
    }

    [Fact]
    public async Task IntegrityCheck_ReportsMissingDatabaseFile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(fixture.Options);

        await Should.ThrowAsync<FileNotFoundException>(() => new SqliteIntegrityChecker(factory, fixture.Options).CheckAsync(cancellationToken));
    }

    [Fact]
    public async Task IntegrityCheck_ReportsForeignKeyViolations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TemporaryDatabase();
        using (var setup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Options.DataSource, Pooling = false }.ConnectionString))
        {
            setup.Open();
            Execute(setup, "PRAGMA foreign_keys=OFF; CREATE TABLE parent (id INTEGER PRIMARY KEY); CREATE TABLE child (parent_id INTEGER NOT NULL REFERENCES parent(id)); INSERT INTO child VALUES (999);");
        }

        var result = await new SqliteIntegrityChecker(new SqliteConnectionFactory(fixture.Options), fixture.Options).CheckAsync(cancellationToken);

        result.IntegrityResult.ShouldBe("ok", StringCompareShould.IgnoreCase);
        result.ForeignKeyViolationCount.ShouldBe(1);
        result.IsHealthy.ShouldBeFalse();
    }

    [Fact]
    public async Task BackupAndRestore_PreserveWalDataAndPassIntegrity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(fixture.Options);
        using (var connection = factory.OpenConnection())
        {
            Execute(connection, "CREATE TABLE sample (value TEXT NOT NULL); INSERT INTO sample VALUES ('round-trip');");
        }

        var backup = Path.Combine(fixture.DirectoryPath, "backup.sqlite");
        var restored = Path.Combine(fixture.DirectoryPath, "restored.sqlite");
        var backupService = new SqliteBackupService(factory, fixture.Options);
        await backupService.BackupAsync(backup, cancellationToken: cancellationToken);
        await backupService.RestoreAsync(backup, restored, cancellationToken);

        var restoredFactory = new SqliteConnectionFactory(new SqlitePlatformOptions { DataSource = restored });
        var result = await new SqliteIntegrityChecker(restoredFactory, new SqlitePlatformOptions { DataSource = restored }).CheckAsync(cancellationToken);
        result.IsHealthy.ShouldBeTrue();
        using var restoredConnection = restoredFactory.OpenConnection();
        Convert.ToString(Scalar(restoredConnection, "SELECT value FROM sample;"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe("round-trip");
    }

    [Fact]
    public async Task Restore_ReportsMissingSourceAndSamePath()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TemporaryDatabase();
        var service = new SqliteBackupService(new SqliteConnectionFactory(fixture.Options), fixture.Options);
        var missing = Path.Combine(fixture.DirectoryPath, "missing.sqlite");

        await Should.ThrowAsync<FileNotFoundException>(() => service.RestoreAsync(missing, Path.Combine(fixture.DirectoryPath, "restored.sqlite"), cancellationToken));
        using var liveConnection = new SqliteConnectionFactory(fixture.Options).OpenConnection();
        await Should.ThrowAsync<ArgumentException>(() => service.RestoreAsync(fixture.Options.DataSource, fixture.Options.DataSource, cancellationToken));
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    public async Task BackupAndRestore_RejectLiveDatabaseSidecars(string suffix)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(fixture.Options);
        using (factory.OpenConnection())
        {
        }

        var service = new SqliteBackupService(factory, fixture.Options);
        var sidecar = fixture.Options.DataSource + suffix;
        var safePath = Path.Combine(fixture.DirectoryPath, "safe.sqlite");

        await Should.ThrowAsync<ArgumentException>(() => service.BackupAsync(sidecar, overwrite: true, cancellationToken));
        await Should.ThrowAsync<ArgumentException>(() => service.RestoreAsync(sidecar, safePath, cancellationToken));
        await Should.ThrowAsync<ArgumentException>(() => service.RestoreAsync(safePath, sidecar, cancellationToken));
    }

    [Fact]
    public async Task Backup_RejectsExistingDestinationUnlessOverwriteIsRequested()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(fixture.Options);
        using (var connection = factory.OpenConnection())
        {
            Execute(connection, "CREATE TABLE sample (value TEXT NOT NULL); INSERT INTO sample VALUES ('source');");
        }

        var destination = Path.Combine(fixture.DirectoryPath, "backup.sqlite");
        File.WriteAllText(destination, "preserve me", System.Text.Encoding.UTF8);
        var service = new SqliteBackupService(factory, fixture.Options);
        await Should.ThrowAsync<IOException>(() => service.BackupAsync(destination, cancellationToken: cancellationToken));
        File.ReadAllText(destination, System.Text.Encoding.UTF8).ShouldBe("preserve me");

        File.Delete(destination);
        using (var existing = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ConnectionString))
        {
            existing.Open();
            Execute(existing, "CREATE TABLE old_data (value TEXT NOT NULL); INSERT INTO old_data VALUES ('old');");
        }
        await service.BackupAsync(destination, overwrite: true, cancellationToken: cancellationToken);
        using var restored = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ConnectionString);
        restored.Open();
        Convert.ToString(Scalar(restored, "SELECT value FROM sample;"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe("source");
    }

    [Fact]
    public async Task Restore_PreservesDestinationWhenSourceIsInvalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TemporaryDatabase();
        var service = new SqliteBackupService(new SqliteConnectionFactory(fixture.Options), fixture.Options);
        var source = Path.Combine(fixture.DirectoryPath, "invalid.sqlite");
        var destination = Path.Combine(fixture.DirectoryPath, "destination.sqlite");
        File.WriteAllText(source, "not a sqlite database", System.Text.Encoding.UTF8);
        File.WriteAllText(destination, "existing destination", System.Text.Encoding.UTF8);

        await Should.ThrowAsync<Exception>(() => service.RestoreAsync(source, destination, cancellationToken));
        File.ReadAllText(destination, System.Text.Encoding.UTF8).ShouldBe("existing destination");

        File.Delete(destination);
        await Should.ThrowAsync<Exception>(() => service.RestoreAsync(source, destination, cancellationToken));
        File.Exists(destination).ShouldBeFalse();
    }

    [Fact]
    public void AddSqlitePlatform_RegistersValidatedSingletonServices()
    {
        using var fixture = new TemporaryDatabase();
        var services = new ServiceCollection();
        services.AddSqlitePlatform(fixture.Options.DataSource, options => options.BusyTimeoutMilliseconds = 321);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<SqlitePlatformOptions>().DataSource.ShouldBe(fixture.Options.DataSource);
        provider.GetRequiredService<SqliteConnectionFactory>().ShouldBeSameAs(provider.GetRequiredService<SqliteConnectionFactory>());
        provider.GetRequiredService<SqliteRetryPolicy>().ShouldBeSameAs(provider.GetRequiredService<SqliteRetryPolicy>());
        provider.GetRequiredService<SqliteMigrationRunner>().ShouldNotBeNull();
        provider.GetRequiredService<SqliteIntegrityChecker>().ShouldNotBeNull();
        provider.GetRequiredService<SqliteBackupService>().ShouldNotBeNull();
        provider.GetRequiredService<SqlitePlatformOptions>().BusyTimeoutMilliseconds.ShouldBe(321);
    }

    private static SqliteMigrationRunner CreateRunner(TemporaryDatabase fixture) =>
        new(new SqliteConnectionFactory(fixture.Options), fixture.Options);

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "incursa-sqlite-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            Options = new SqlitePlatformOptions { DataSource = Path.Combine(DirectoryPath, "database.sqlite") };
        }

        public string DirectoryPath { get; }

        public SqlitePlatformOptions Options { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
