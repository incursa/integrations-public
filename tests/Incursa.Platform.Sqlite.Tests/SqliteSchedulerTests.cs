// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform;
using Xunit;

namespace Incursa.Platform.Sqlite.Tests;

public sealed class SqliteSchedulerTests
{
    [Fact]
    public async Task TimerClaimAckAndWrongOwnerAreAtomic()
    {
        var path = NewPath();
        try
        {
            var client = Create(path);
            var timer = await client.ScheduleTimerAsync("scheduler.test", "payload", DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);
            var owner = OwnerToken.GenerateNew();
            var claimed = await client.ClaimTimersAsync(owner, 30, 10, CancellationToken.None);
            Assert.Single(claimed);
            await client.AckTimersAsync(OwnerToken.GenerateNew(), claimed, CancellationToken.None);
            Assert.Empty(await client.ClaimTimersAsync(OwnerToken.GenerateNew(), 30, 10, CancellationToken.None));
            await client.AckTimersAsync(owner, claimed, CancellationToken.None);
            Assert.Equal(timer, claimed[0].ToString());
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task RecurringJobCanBeTriggeredAndUpdated()
    {
        var path = NewPath();
        try
        {
            var client = Create(path);
            await client.CreateOrUpdateJobAsync("job", "old", "*/1 * * * * *", "one", CancellationToken.None);
            await client.CreateOrUpdateJobAsync("job", "new", "*/1 * * * * *", "two", CancellationToken.None);
            await client.TriggerJobAsync("job", CancellationToken.None);
            var owner = OwnerToken.GenerateNew();
            var ids = await client.ClaimJobRunsAsync(owner, 30, 10, CancellationToken.None);
            Assert.Single(ids);
            await client.AckJobRunsAsync(owner, ids, CancellationToken.None);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ConcurrentClaimsDoNotDuplicateWork()
    {
        var path = NewPath();
        try
        {
            var client = Create(path);
            for (var i = 0; i < 12; i++) await client.ScheduleTimerAsync("scheduler.test", i.ToString(System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);
            var owners = Enumerable.Range(0, 4).Select(_ => OwnerToken.GenerateNew()).ToArray();
            var batches = await Task.WhenAll(owners.Select(owner => client.ClaimTimersAsync(owner, 30, 12, CancellationToken.None)));
            Assert.Equal(12, batches.SelectMany(static x => x).Distinct().Count());
            Assert.Equal(12, batches.Sum(static x => x.Count));
        }
        finally { Delete(path); }
    }

    private static SqliteSchedulerClient Create(string path)
    {
        var options = new SqlitePlatformOptions { DataSource = path, RetryBaseDelay = TimeSpan.FromMilliseconds(1), MaxRetryCount = 10 };
        var factory = new SqliteConnectionFactory(options);
        return new SqliteSchedulerClient(factory, new SqliteRetryPolicy(options));
    }

    private static string NewPath() => Path.Combine(Path.GetTempPath(), "incursa-sqlite-scheduler-" + Guid.NewGuid().ToString("N") + ".db");

    private static void Delete(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(path + suffix); } catch (IOException) { }
        }
    }
}
