// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform.Sqlite.Tests;

[Trait("Category", "Unit")]
public sealed class SqliteIdempotencyTests
{
    [Fact]
    public async Task ConcurrentBeginHasOneWinner()
    {
        using var database = new TestDatabase();
        var stores = Enumerable.Range(0, 12).Select(_ => database.CreateStore()).ToArray();
        var results = await Task.WhenAll(stores.Select(store => store.TryBeginAsync("key", TestContext.Current.CancellationToken)));
        results.Count(static result => result).ShouldBe(1);
    }

    [Fact]
    public async Task CompleteBlocksDuplicateAndFailureAllowsRetry()
    {
        using var database = new TestDatabase();
        var store = database.CreateStore();
        (await store.TryBeginAsync("complete", TestContext.Current.CancellationToken)).ShouldBeTrue();
        await store.CompleteAsync("complete", TestContext.Current.CancellationToken);
        (await store.TryBeginAsync("complete", TestContext.Current.CancellationToken)).ShouldBeFalse();

        (await store.TryBeginAsync("failure", TestContext.Current.CancellationToken)).ShouldBeTrue();
        await store.FailAsync("failure", TestContext.Current.CancellationToken);
        (await store.TryBeginAsync("failure", TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task ExpiredReservationIsReclaimedAndCleanupRemovesCompletedRows()
    {
        using var database = new TestDatabase();
        var store = database.CreateStore();
        (await store.TryBeginAsync("expired", TestContext.Current.CancellationToken)).ShouldBeTrue();
        using (var connection = database.OpenRaw())
        {
            Execute(connection, "UPDATE \"SqliteIdempotency\" SET LockedUntilUtc=$expired WHERE IdempotencyKey='expired';", ("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")));
        }

        (await store.TryBeginAsync("expired", TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await store.TryBeginAsync("completed", TestContext.Current.CancellationToken)).ShouldBeTrue();
        await store.CompleteAsync("completed", TestContext.Current.CancellationToken);
        using (var connection = database.OpenRaw())
        {
            Execute(connection, "UPDATE \"SqliteIdempotency\" SET CompletedAtUtc=$old WHERE IdempotencyKey='completed';", ("$old", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")));
        }

        (await store.CleanupAsync(TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken)).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task StaleWorkerCannotCompleteOrFailAReclaimedReservation()
    {
        using var database = new TestDatabase();
        var stale = database.CreateStore();
        var current = database.CreateStore();
        (await stale.TryBeginAsync("reclaimed", TestContext.Current.CancellationToken)).ShouldBeTrue();

        using (var connection = database.OpenRaw())
        {
            Execute(connection, "UPDATE \"SqliteIdempotency\" SET LockedUntilUtc=$expired WHERE IdempotencyKey='reclaimed';", ("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")));
        }

        (await current.TryBeginAsync("reclaimed", TestContext.Current.CancellationToken)).ShouldBeTrue();
        await stale.CompleteAsync("reclaimed", TestContext.Current.CancellationToken);
        await stale.FailAsync("reclaimed", TestContext.Current.CancellationToken);

        using (var connection = database.OpenRaw())
        {
            Scalar(connection, "SELECT LockedUntilUtc FROM \"SqliteIdempotency\" WHERE IdempotencyKey='reclaimed';").ShouldNotBeNull();
            Convert.ToInt32(Scalar(connection, "SELECT Status FROM \"SqliteIdempotency\" WHERE IdempotencyKey='reclaimed';"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
        }

        await current.CompleteAsync("reclaimed", TestContext.Current.CancellationToken);
        (await current.TryBeginAsync("reclaimed", TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "incursa-sqlite-idempotency-" + Guid.NewGuid().ToString("N"));

        public TestDatabase()
        {
            Directory.CreateDirectory(directory);
        }

        public SqliteIdempotencyStore CreateStore() => new(
            new SqliteConnectionFactory(new SqlitePlatformOptions
            {
                DataSource = Path.Combine(directory, "database.sqlite"),
                BusyTimeoutMilliseconds = 1_000,
                MaxRetryCount = 20,
                RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            }),
            new SqliteRetryPolicy(new SqlitePlatformOptions
            {
                DataSource = Path.Combine(directory, "database.sqlite"),
                BusyTimeoutMilliseconds = 1_000,
                MaxRetryCount = 20,
                RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            }));

        public SqliteConnection OpenRaw()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "database.sqlite"), Pooling = false }.ConnectionString);
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }
}
