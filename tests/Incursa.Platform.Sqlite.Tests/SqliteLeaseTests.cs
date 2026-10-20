// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform.Sqlite.Tests;

[Trait("Category", "Unit")]
public sealed class SqliteLeaseTests
{
    [Fact]
    public async Task ConcurrentAcquireHasOneWinnerAndRenewsWithNewFence()
    {
        using var database = new TestDatabase();
        var first = database.CreateLeaseFactory();
        var second = database.CreateLeaseFactory();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            (index & 1) == 0
                ? first.AcquireAsync("resource", TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken)
                : second.AcquireAsync("resource", TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken)));

        var winner = results.Single(static lease => lease is not null)!;
        results.Count(static lease => lease is not null).ShouldBe(1);
        var initialFence = winner.FencingToken;
        (await winner.TryRenewNowAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        winner.FencingToken.ShouldBeGreaterThan(initialFence);
        await winner.DisposeAsync();
    }

    [Fact]
    public async Task ExpiredLeaseCanBeReclaimedAndStaleOwnerCannotReleaseIt()
    {
        using var database = new TestDatabase();
        var factory = database.CreateLeaseFactory();
        var ownerA = await factory.AcquireAsync("resource", TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        ownerA.ShouldNotBeNull();

        using (var connection = database.OpenRaw())
        {
            Execute(connection, "UPDATE \"SqliteLeases\" SET LeaseUntilUtc=$expired WHERE ResourceName=$resource;", ("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")), ("$resource", "resource"));
        }

        var ownerB = await factory.AcquireAsync("resource", TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        ownerB.ShouldNotBeNull();
        ownerB!.FencingToken.ShouldBeGreaterThan(ownerA!.FencingToken);
        await ownerA.DisposeAsync();
        using var verify = database.OpenRaw();
        Convert.ToString(Scalar(verify, "SELECT OwnerToken FROM \"SqliteLeases\" WHERE ResourceName='resource';"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(ownerB.OwnerToken.Value.ToString("N"));
        await ownerB.DisposeAsync();
    }

    [Fact]
    public async Task SameOwnerReacquireAdvancesFenceAndOldLeaseCannotReleaseNewLease()
    {
        using var database = new TestDatabase();
        var factory = database.CreateLeaseFactory();
        var owner = OwnerToken.GenerateNew();
        var first = await factory.AcquireAsync("same-owner", TimeSpan.FromSeconds(10), ownerToken: owner, cancellationToken: TestContext.Current.CancellationToken);
        first.ShouldNotBeNull();
        var second = await factory.AcquireAsync("same-owner", TimeSpan.FromSeconds(10), ownerToken: owner, cancellationToken: TestContext.Current.CancellationToken);
        second.ShouldNotBeNull();
        second!.FencingToken.ShouldBeGreaterThan(first!.FencingToken);

        await first.DisposeAsync();
        using (var connection = database.OpenRaw())
        {
            Convert.ToString(Scalar(connection, "SELECT OwnerToken FROM \"SqliteLeases\" WHERE ResourceName='same-owner';"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(owner.Value.ToString("N"));
        }

        await second.DisposeAsync();
    }

    [Fact]
    public async Task CancellationMakesLeaseLost()
    {
        using var database = new TestDatabase();
        using var cancellation = new CancellationTokenSource();
        var lease = await database.CreateLeaseFactory().AcquireAsync("resource", TimeSpan.FromSeconds(10), cancellationToken: cancellation.Token);
        lease.ShouldNotBeNull();
        cancellation.Cancel();
        Should.Throw<LostLeaseException>(() => lease!.ThrowIfLost());
        (await lease.TryRenewNowAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
        await lease.DisposeAsync();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
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

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "incursa-sqlite-lease-" + Guid.NewGuid().ToString("N"));

        public TestDatabase()
        {
            Directory.CreateDirectory(directory);
        }

        public SqliteLeaseFactory CreateLeaseFactory() => new(
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
