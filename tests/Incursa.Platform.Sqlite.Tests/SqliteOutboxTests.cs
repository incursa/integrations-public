// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform;
using Incursa.Platform.Outbox;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform.Sqlite.Tests;

[Trait("Category", "Unit")]
public sealed class SqliteOutboxTests
{
    [Fact]
    public async Task EnqueueClaimAckAndRetryAreDurable()
    {
        using var database = new TestDatabase();
        var store = database.CreateStore();
        var service = database.CreateService(store);

        await service.EnqueueAsync("orders", "{\"id\":1}", "correlation-1", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        var claimed = await store.ClaimDueAsync(10, TestContext.Current.CancellationToken);
        claimed.Count.ShouldBe(1);
        claimed[0].Topic.ShouldBe("orders");
        claimed[0].CorrelationId.ShouldBe("correlation-1");
        await store.RescheduleAsync(claimed[0].Id, TimeSpan.Zero, "transient", TestContext.Current.CancellationToken);
        var retried = await store.ClaimDueAsync(10, TestContext.Current.CancellationToken);
        retried.Count.ShouldBe(1);
        retried[0].RetryCount.ShouldBe(1);
        await store.MarkDispatchedAsync(retried[0].Id, TestContext.Current.CancellationToken);
        (await store.ClaimDueAsync(10, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task CallerTransactionRollsBackBusinessAndOutboxTogether()
    {
        using var database = new TestDatabase();
        var store = database.CreateStore();
        var service = database.CreateService(store);
        await store.ClaimDueAsync(1, TestContext.Current.CancellationToken);
        using var connection = database.Factory.OpenConnection();
        Execute(connection, "CREATE TABLE business (id INTEGER PRIMARY KEY);");
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "INSERT INTO business VALUES (1);");
        await service.EnqueueAsync("business.changed", "payload", transaction, TestContext.Current.CancellationToken);
        transaction.Rollback();

        using var verify = database.Factory.OpenConnection();
        Scalar(verify, "SELECT COUNT(*) FROM business;").ShouldBe(0);
        Scalar(verify, "SELECT COUNT(*) FROM \"Outbox\";").ShouldBe(0);
    }

    [Fact]
    public async Task ConcurrentClaimsProduceOneOwnerPerMessage()
    {
        using var database = new TestDatabase();
        var first = database.CreateStore();
        var second = database.CreateStore();
        var service = database.CreateService(first);
        for (var i = 0; i < 20; i++) await service.EnqueueAsync("work", i.ToString(System.Globalization.CultureInfo.InvariantCulture), TestContext.Current.CancellationToken);

        var claimed = await Task.WhenAll(
            first.ClaimAsync(OwnerToken.GenerateNew(), 30, 20, TestContext.Current.CancellationToken),
            second.ClaimAsync(OwnerToken.GenerateNew(), 30, 20, TestContext.Current.CancellationToken));
        claimed.SelectMany(static ids => ids).Distinct().Count().ShouldBe(20);
        claimed.Sum(static ids => ids.Count).ShouldBe(20);
    }

    [Fact]
    public async Task StaleOwnerCannotAcknowledgeReclaimedWork()
    {
        using var database = new TestDatabase();
        var first = database.CreateStore();
        var second = database.CreateStore();
        var service = database.CreateService(first);
        await service.EnqueueAsync("work", "payload", TestContext.Current.CancellationToken);
        var ownerA = OwnerToken.GenerateNew();
        var ownerB = OwnerToken.GenerateNew();
        var id = (await first.ClaimAsync(ownerA, 1, 1, TestContext.Current.CancellationToken)).Single();
        using (var raw = database.Factory.OpenConnection())
        {
            Execute(raw, $"UPDATE \"Outbox\" SET \"LockedUntil\"=$expired WHERE \"Id\"=$id;", ("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")), ("$id", id.Value.ToString("D")));
        }

        await first.ReapExpiredAsync(TestContext.Current.CancellationToken);
        (await second.ClaimAsync(ownerB, 30, 1, TestContext.Current.CancellationToken)).Single().ShouldBe(id);
        await first.AckAsync(ownerA, [id], TestContext.Current.CancellationToken);
        using var verify = database.Factory.OpenConnection();
        Scalar(verify, "SELECT Status FROM \"Outbox\" WHERE \"Id\"=$id;", ("$id", id.Value.ToString("D"))).ShouldBe(1);
    }

    [Fact]
    public async Task JoinCompletionIsIdempotentAndOutboxAckPropagates()
    {
        using var database = new TestDatabase();
        var store = database.CreateStore();
        var joins = database.CreateJoinStore();
        var service = database.CreateService(store, joins);
        var joinId = await service.StartJoinAsync(7, 1, "{\"kind\":\"one\"}", TestContext.Current.CancellationToken);
        await service.EnqueueAsync("step", "payload", TestContext.Current.CancellationToken);
        var message = (await store.ClaimDueAsync(1, TestContext.Current.CancellationToken)).Single();
        await joins.AttachMessageToJoinAsync(joinId, message.MessageId, TestContext.Current.CancellationToken);
        await joins.IncrementCompletedAsync(joinId, message.MessageId, TestContext.Current.CancellationToken);
        await joins.IncrementCompletedAsync(joinId, message.MessageId, TestContext.Current.CancellationToken);
        var join = (await joins.GetJoinAsync(joinId, TestContext.Current.CancellationToken))!;
        join.CompletedSteps.ShouldBe(1);
        join.FailedSteps.ShouldBe(0);
        (await joins.GetJoinMessagesAsync(joinId, TestContext.Current.CancellationToken)).ShouldContain(message.MessageId);
    }

    private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters) => Execute(connection, null, sql, parameters);

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "incursa-sqlite-outbox-" + Guid.NewGuid().ToString("N"));

        public TestDatabase()
        {
            Directory.CreateDirectory(directory);
            Factory = new SqliteConnectionFactory(new SqlitePlatformOptions
            {
                DataSource = Path.Combine(directory, "database.sqlite"),
                BusyTimeoutMilliseconds = 1_000,
                MaxRetryCount = 20,
                RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            });
            Retry = new SqliteRetryPolicy(new SqlitePlatformOptions { DataSource = Factory.DataSource, BusyTimeoutMilliseconds = 1_000, MaxRetryCount = 20, RetryBaseDelay = TimeSpan.FromMilliseconds(1) });
        }

        public SqliteConnectionFactory Factory { get; }

        public SqliteRetryPolicy Retry { get; }

        public SqliteOutboxStore CreateStore() => new(Factory, Retry);

        public SqliteOutboxJoinStore CreateJoinStore() => new(Factory, Retry);

        public SqliteOutboxService CreateService(SqliteOutboxStore store, IOutboxJoinStore? joins = null) => new(Factory, Retry, store, joins);

        public void Dispose()
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }
}
