// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform;
using Shouldly;

namespace Incursa.Platform.Sqlite.Tests;

[Trait("Category", "Unit")]
public sealed class SqliteFanoutRepositoriesTests
{
    [Fact]
    public async Task PolicyRepository_ReturnsDefaultsAndAtomicallyUpsertsPolicy()
    {
        using var database = new TemporaryDatabase();
        var repository = database.CreatePolicyRepository();

        (await repository.GetCadenceAsync("billing", "daily", TestContext.Current.CancellationToken)).ShouldBe((300, 60));

        await repository.SetCadenceAsync("billing", "daily", 15, 3, TestContext.Current.CancellationToken);
        (await repository.GetCadenceAsync("billing", "daily", TestContext.Current.CancellationToken)).ShouldBe((15, 3));

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => repository.SetCadenceAsync("billing", "daily", 0, 0, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => repository.SetCadenceAsync("billing", "daily", 1, -1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CursorRepository_AdvancesMonotonicallyAndIsIdempotent()
    {
        using var database = new TemporaryDatabase();
        var repository = database.CreateCursorRepository();
        var older = DateTimeOffset.UtcNow.AddMinutes(-1);
        var newer = older.AddMinutes(1);

        await repository.MarkCompletedAsync("billing", "daily", "shard-1", newer, TestContext.Current.CancellationToken);
        await repository.MarkCompletedAsync("billing", "daily", "shard-1", older, TestContext.Current.CancellationToken);
        await repository.MarkCompletedAsync("billing", "daily", "shard-1", newer, TestContext.Current.CancellationToken);

        (await repository.GetLastAsync("billing", "daily", "shard-1", TestContext.Current.CancellationToken)).ShouldBe(newer.ToUniversalTime());
        (await repository.GetLastAsync("billing", "daily", "missing", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Repositories_SupportConcurrentLocalWriters()
    {
        using var database = new TemporaryDatabase();
        var policies = Enumerable.Range(0, 16).Select(_ => database.CreatePolicyRepository()).ToArray();
        var cursors = Enumerable.Range(0, 16).Select(_ => database.CreateCursorRepository()).ToArray();
        var cancellationToken = TestContext.Current.CancellationToken;

        await Task.WhenAll(policies.Select((repository, index) => repository.SetCadenceAsync("topic", "work", index + 1, index, cancellationToken)));
        await Task.WhenAll(cursors.Select((repository, index) => repository.MarkCompletedAsync("topic", "work", "shard", DateTimeOffset.UtcNow.AddSeconds(index), cancellationToken)));

        var policy = await policies[0].GetCadenceAsync("topic", "work", cancellationToken);
        policy.everySeconds.ShouldBeInRange(1, 16);
        policy.jitterSeconds.ShouldBe(policy.everySeconds - 1);
        var cursor = await cursors[0].GetLastAsync("topic", "work", "shard", cancellationToken);
        cursor.ShouldNotBeNull();
        cursor.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(-30));
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "incursa-sqlite-fanout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            Options = new SqlitePlatformOptions
            {
                DataSource = Path.Combine(DirectoryPath, "database.sqlite"),
                BusyTimeoutMilliseconds = 1_000,
                MaxRetryCount = 20,
                RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            };
            Factory = new SqliteConnectionFactory(Options);
            RetryPolicy = new SqliteRetryPolicy(Options);
        }

        public string DirectoryPath { get; }

        public SqlitePlatformOptions Options { get; }

        private SqliteConnectionFactory Factory { get; }

        private SqliteRetryPolicy RetryPolicy { get; }

        public SqliteFanoutPolicyRepository CreatePolicyRepository() => new(Factory, RetryPolicy);

        public SqliteFanoutCursorRepository CreateCursorRepository() => new(Factory, RetryPolicy);

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
                // SQLite sidecars can remain briefly after a concurrent test operation.
            }
        }
    }
}
