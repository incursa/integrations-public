// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform;
using Incursa.Platform.Correlation;
using Incursa.Platform.Operations;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform.Sqlite.Tests;

[Trait("Category", "Unit")]
public sealed class SqliteOperationsTests
{
    [Fact]
    public async Task Tracker_PersistsLifecycleCorrelationTagsAndEvents()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var tracker = database.CreateTracker(clock);
        var correlation = new CorrelationContext(new CorrelationId("corr-1"), new CorrelationId("cause-1"), "trace-1", "span-1", clock.GetUtcNow(), new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = "t-1" });

        var operationId = await tracker.StartAsync(" Import ", correlation, null, new Dictionary<string, string>(StringComparer.Ordinal) { ["source"] = "test" }, TestContext.Current.CancellationToken);
        var pending = await tracker.GetSnapshotAsync(operationId, TestContext.Current.CancellationToken);
        pending.ShouldNotBeNull();
        pending!.Status.ShouldBe(OperationStatus.Pending);
        pending.Name.ShouldBe("Import");
        pending.Correlation!.CorrelationId.Value.ShouldBe("corr-1");
        pending.Tags!["source"].ShouldBe("test");

        clock.Advance(TimeSpan.FromSeconds(5));
        await tracker.UpdateProgressAsync(operationId, 42.5, " halfway ", TestContext.Current.CancellationToken);
        await tracker.AddEventAsync(operationId, "Info", "Started child work", "{\"count\":1}", TestContext.Current.CancellationToken);
        await tracker.CompleteAsync(operationId, OperationStatus.Succeeded, " done ", TestContext.Current.CancellationToken);

        var completed = await tracker.GetSnapshotAsync(operationId, TestContext.Current.CancellationToken);
        completed.ShouldNotBeNull();
        completed!.Status.ShouldBe(OperationStatus.Succeeded);
        completed.PercentComplete.ShouldBe(42.5);
        completed.Message.ShouldBe("done");
        completed.CompletedAtUtc.ShouldBe(clock.GetUtcNow());

        using var connection = database.Factory.OpenConnection();
        ScalarInt(connection, "SELECT COUNT(*) FROM OperationEvents WHERE OperationId=$id", ("$id", operationId.Value)).ShouldBe(1);
    }

    [Fact]
    public async Task Watcher_FindsAndMarksOnlyStaleActiveOperations()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var tracker = database.CreateTracker(clock);
        var watcher = database.CreateWatcher(clock);
        var stale = await tracker.StartAsync("stale", null, null, null, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(10));
        var fresh = await tracker.StartAsync("fresh", null, null, null, TestContext.Current.CancellationToken);

        var found = await watcher.FindStalledAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        found.Select(snapshot => snapshot.OperationId).ShouldContain(stale);
        found.Select(snapshot => snapshot.OperationId).ShouldNotContain(fresh);

        await watcher.MarkStalledAsync(stale, TestContext.Current.CancellationToken);
        var snapshot = await tracker.GetSnapshotAsync(stale, TestContext.Current.CancellationToken);
        snapshot!.Status.ShouldBe(OperationStatus.Stalled);
        snapshot.CompletedAtUtc.ShouldBe(clock.GetUtcNow());

        // A second mark is a safe no-op after the terminal transition.
        await watcher.MarkStalledAsync(stale, TestContext.Current.CancellationToken);
        (await tracker.GetSnapshotAsync(stale, TestContext.Current.CancellationToken))!.Status.ShouldBe(OperationStatus.Stalled);
    }

    [Fact]
    public async Task Tracker_SupportsConcurrentLocalWriters()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var tracker = database.CreateTracker(clock);
        var operationId = await tracker.StartAsync("concurrent", null, null, null, TestContext.Current.CancellationToken);
        var trackers = Enumerable.Range(0, 12).Select(_ => database.CreateTracker(clock)).ToArray();

        await Task.WhenAll(trackers.Select((item, index) => item.UpdateProgressAsync(operationId, index, $"progress-{index}", TestContext.Current.CancellationToken)));

        var snapshot = await tracker.GetSnapshotAsync(operationId, TestContext.Current.CancellationToken);
        snapshot.ShouldNotBeNull();
        snapshot!.Status.ShouldBe(OperationStatus.Running);
        snapshot.PercentComplete!.Value.ShouldBeInRange(0d, 11d);
        snapshot.Message.ShouldStartWith("progress-");
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "incursa-sqlite-operations-" + Guid.NewGuid().ToString("N"));
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
        public SqliteConnectionFactory Factory { get; }
        private SqliteRetryPolicy RetryPolicy { get; }

        public SqliteOperationTracker CreateTracker(TimeProvider clock) => new(Factory, RetryPolicy, timeProvider: clock);
        public SqliteOperationWatcher CreateWatcher(TimeProvider clock) => new(Factory, RetryPolicy, timeProvider: clock);

        public void Dispose()
        {
            try { Directory.Delete(DirectoryPath, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow.ToUniversalTime();
        }

        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan amount) => utcNow = utcNow.Add(amount);
    }

    private static int ScalarInt(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
