// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform.Sqlite.Tests;

[Trait("Category", "Unit")]
public sealed class SqliteInboxTests
{
    [Fact]
    public async Task EnqueueDeduplicatesAndTracksProcessedState()
    {
        using var database = new TestDatabase();
        await database.EnsureSchemaAsync(TestContext.Current.CancellationToken);
        var inbox = database.CreateInbox();

        await inbox.EnqueueAsync("orders", "test", "message-1", "payload", [1, 2, 3], TestContext.Current.CancellationToken);
        (await inbox.AlreadyProcessedAsync("message-1", "test", [1, 2, 3], TestContext.Current.CancellationToken)).ShouldBeFalse();
        await inbox.MarkProcessedAsync("message-1", TestContext.Current.CancellationToken);
        (await inbox.AlreadyProcessedAsync("message-1", "test", [1, 2, 3], TestContext.Current.CancellationToken)).ShouldBeTrue();

        var message = await database.CreateStore().GetAsync("message-1", TestContext.Current.CancellationToken);
        message.Topic.ShouldBe("orders");
        message.Payload.ShouldBe("payload");
        message.Hash.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task ConcurrentClaimsAssignEachMessageToAtMostOneOwner()
    {
        using var database = new TestDatabase();
        await database.EnsureSchemaAsync(TestContext.Current.CancellationToken);
        var inbox = database.CreateInbox();
        for (var index = 0; index < 24; index++)
        {
            await inbox.EnqueueAsync("topic", "test", "message-" + index, "payload", TestContext.Current.CancellationToken);
        }

        var stores = Enumerable.Range(0, 8).Select(_ => database.CreateStore()).ToArray();
        var claims = await Task.WhenAll(stores.Select((store, index) =>
            store.ClaimAsync(OwnerToken.GenerateNew(), 30, 24, TestContext.Current.CancellationToken)));
        claims.SelectMany(static ids => ids).Distinct(StringComparer.Ordinal).Count().ShouldBe(24);
        claims.Sum(static ids => ids.Count).ShouldBe(24);
    }

    [Fact]
    public async Task StaleOwnerCannotAcknowledgeAfterLeaseRecovery()
    {
        using var database = new TestDatabase();
        await database.EnsureSchemaAsync(TestContext.Current.CancellationToken);
        await database.CreateInbox().EnqueueAsync("topic", "test", "message-1", "payload", TestContext.Current.CancellationToken);
        var first = database.CreateStore();
        var second = database.CreateStore();
        var ownerA = OwnerToken.GenerateNew();
        var ownerB = OwnerToken.GenerateNew();
        var claimedA = await first.ClaimAsync(ownerA, 30, 1, TestContext.Current.CancellationToken);
        claimedA.ShouldContain("message-1");

        using (var connection = database.OpenRaw())
        {
            Execute(connection, "UPDATE \"Inbox\" SET \"LockedUntil\"=$expired WHERE \"MessageId\"='message-1';", ("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")));
        }

        var claimedB = await second.ClaimAsync(ownerB, 30, 1, TestContext.Current.CancellationToken);
        claimedB.ShouldContain("message-1");
        await first.AckAsync(ownerA, claimedA, TestContext.Current.CancellationToken);
        await second.AckAsync(ownerB, claimedB, TestContext.Current.CancellationToken);

        using var verify = database.OpenRaw();
        Scalar(verify, "SELECT Status FROM \"Inbox\" WHERE MessageId='message-1';").ShouldBe("Done");
        Scalar(verify, "SELECT OwnerToken FROM \"Inbox\" WHERE MessageId='message-1';").ShouldBeNull();
    }

    [Fact]
    public async Task ReapExpiredReturnsInterruptedWorkToTheQueue()
    {
        using var database = new TestDatabase();
        await database.EnsureSchemaAsync(TestContext.Current.CancellationToken);
        await database.CreateInbox().EnqueueAsync("topic", "test", "message-1", "payload", TestContext.Current.CancellationToken);
        var store = database.CreateStore();
        var owner = OwnerToken.GenerateNew();
        (await store.ClaimAsync(owner, 30, 1, TestContext.Current.CancellationToken)).ShouldContain("message-1");
        using (var connection = database.OpenRaw())
        {
            Execute(connection, "UPDATE \"Inbox\" SET \"LockedUntil\"=$expired WHERE MessageId='message-1';", ("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")));
        }

        await store.ReapExpiredAsync(TestContext.Current.CancellationToken);
        var recovered = await store.ClaimAsync(OwnerToken.GenerateNew(), 30, 1, TestContext.Current.CancellationToken);
        recovered.ShouldContain("message-1");
    }

    [Fact]
    public async Task AbandonDelayAndReapAreDurableAcrossNewStoreInstances()
    {
        using var database = new TestDatabase();
        await database.EnsureSchemaAsync(TestContext.Current.CancellationToken);
        var inbox = database.CreateInbox();
        await inbox.EnqueueAsync("topic", "test", "message-1", "payload", TestContext.Current.CancellationToken);
        var owner = OwnerToken.GenerateNew();
        var first = database.CreateStore();
        var claimed = await first.ClaimAsync(owner, 30, 1, TestContext.Current.CancellationToken);
        await first.AbandonAsync(owner, claimed, "transient", TimeSpan.FromHours(1), TestContext.Current.CancellationToken);
        (await database.CreateStore().ClaimAsync(OwnerToken.GenerateNew(), 30, 1, TestContext.Current.CancellationToken)).ShouldBeEmpty();

        using (var connection = database.OpenRaw())
        {
            Execute(connection, "UPDATE \"Inbox\" SET \"DueTimeUtc\"=$due WHERE MessageId='message-1';", ("$due", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")));
        }

        var restarted = database.CreateStore();
        var recoveryOwner = OwnerToken.GenerateNew();
        var recovered = await restarted.ClaimAsync(recoveryOwner, 30, 1, TestContext.Current.CancellationToken);
        recovered.ShouldContain("message-1");
        await restarted.FailAsync(recoveryOwner, recovered, "permanent", TestContext.Current.CancellationToken);
        await restarted.ReviveAsync(recovered, "manual retry", cancellationToken: TestContext.Current.CancellationToken);
        var revived = await restarted.ClaimAsync(OwnerToken.GenerateNew(), 30, 1, TestContext.Current.CancellationToken);
        revived.ShouldContain("message-1");
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
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
        private readonly string directory = Path.Combine(Path.GetTempPath(), "incursa-sqlite-inbox-" + Guid.NewGuid().ToString("N"));

        public TestDatabase()
        {
            Directory.CreateDirectory(directory);
            Options = new SqlitePlatformOptions
            {
                DataSource = Path.Combine(directory, "database.sqlite"),
                BusyTimeoutMilliseconds = 1_000,
                MaxRetryCount = 20,
                RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            };
        }

        public SqlitePlatformOptions Options { get; }

        public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            var factory = new SqliteConnectionFactory(Options);
            await SqliteInboxSchema.EnsureAsync(new SqliteMigrationRunner(factory, Options), cancellationToken: cancellationToken);
        }

        public SqliteInboxService CreateInbox() => new(CreateFactory(), CreateRetryPolicy(), new SqliteInboxOptions());

        public SqliteInboxWorkStore CreateStore() => new(CreateFactory(), CreateRetryPolicy(), new SqliteInboxOptions());

        public SqliteConnection OpenRaw()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Options.DataSource, Pooling = false }.ConnectionString);
            connection.Open();
            return connection;
        }

        private SqliteConnectionFactory CreateFactory() => new(Options);

        private SqliteRetryPolicy CreateRetryPolicy() => new(Options);

        public void Dispose()
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }
}
