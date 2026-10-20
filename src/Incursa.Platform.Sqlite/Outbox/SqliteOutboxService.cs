// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using System.Data;
using Incursa.Platform.Outbox;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>SQLite implementation of the platform outbox contract.</summary>
internal sealed class SqliteOutboxService : IOutbox
{
    private readonly SqliteConnectionFactory factory;
    private readonly SqliteRetryPolicy retry;
    private readonly SqliteOutboxStore store;
    private readonly IOutboxJoinStore? joinStore;
    private readonly TimeProvider timeProvider;
    private int schemaEnsured;

    public SqliteOutboxService(SqliteConnectionFactory factory, SqliteRetryPolicy retry, SqliteOutboxStore store, IOutboxJoinStore? joinStore = null, TimeProvider? timeProvider = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.retry = retry ?? throw new ArgumentNullException(nameof(retry));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.joinStore = joinStore;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task EnqueueAsync(string topic, string payload, CancellationToken cancellationToken) => EnqueueAsync(topic, payload, correlationId: null, dueTimeUtc: null, cancellationToken: cancellationToken);

    public Task EnqueueAsync(string topic, string payload, string? correlationId, CancellationToken cancellationToken) => EnqueueAsync(topic, payload, correlationId, null, cancellationToken);

    public async Task EnqueueAsync(string topic, string payload, string? correlationId, DateTimeOffset? dueTimeUtc, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        await retry.ExecuteAsync(async ct =>
        {
            using var connection = factory.OpenConnection();
            using var transaction = connection.BeginTransaction();
            try
            {
                await InsertAsync(connection, transaction, topic, payload, correlationId, dueTimeUtc, ct).ConfigureAwait(false);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task EnqueueAsync(string topic, string payload, IDbTransaction transaction, CancellationToken cancellationToken) => EnqueueAsync(topic, payload, transaction, null, null, cancellationToken);

    public Task EnqueueAsync(string topic, string payload, IDbTransaction transaction, string? correlationId, CancellationToken cancellationToken) => EnqueueAsync(topic, payload, transaction, correlationId, null, cancellationToken);

    public async Task EnqueueAsync(string topic, string payload, IDbTransaction transaction, string? correlationId, DateTimeOffset? dueTimeUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(payload);
        var sqliteTransaction = SqliteOutboxDatabase.AsSqliteTransaction(transaction) ?? throw new ArgumentException("Transaction must be a SQLite transaction.", nameof(transaction));
        if (sqliteTransaction.Connection is not SqliteConnection connection)
        {
            throw new ArgumentException("Transaction must have an open SQLite connection.", nameof(transaction));
        }
        await InsertAsync(connection, sqliteTransaction, topic, payload, correlationId, dueTimeUtc, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<OutboxWorkItemIdentifier>> ClaimAsync(OwnerToken ownerToken, int leaseSeconds, int batchSize, CancellationToken cancellationToken) => store.ClaimAsync(ownerToken, leaseSeconds, batchSize, cancellationToken);

    public Task AckAsync(OwnerToken ownerToken, IEnumerable<OutboxWorkItemIdentifier> ids, CancellationToken cancellationToken) => store.AckAsync(ownerToken, ids, cancellationToken);

    public Task AbandonAsync(OwnerToken ownerToken, IEnumerable<OutboxWorkItemIdentifier> ids, CancellationToken cancellationToken) => store.AbandonAsync(ownerToken, ids, error: null, delay: null, cancellationToken);

    public Task FailAsync(OwnerToken ownerToken, IEnumerable<OutboxWorkItemIdentifier> ids, CancellationToken cancellationToken) => store.FailAsync(ownerToken, ids, error: null, cancellationToken);

    public Task ReapExpiredAsync(CancellationToken cancellationToken) => store.ReapExpiredAsync(cancellationToken);

    public async Task<JoinIdentifier> StartJoinAsync(long tenantId, int expectedSteps, string? metadata, CancellationToken cancellationToken)
    {
        var join = await RequireJoinStore().CreateJoinAsync(tenantId, expectedSteps, metadata, cancellationToken).ConfigureAwait(false);
        return join.JoinId;
    }

    public Task AttachMessageToJoinAsync(JoinIdentifier joinId, OutboxMessageIdentifier outboxMessageId, CancellationToken cancellationToken) => RequireJoinStore().AttachMessageToJoinAsync(joinId, outboxMessageId, cancellationToken);

    public Task ReportStepCompletedAsync(JoinIdentifier joinId, OutboxMessageIdentifier outboxMessageId, CancellationToken cancellationToken) => RequireJoinStore().IncrementCompletedAsync(joinId, outboxMessageId, cancellationToken);

    public Task ReportStepFailedAsync(JoinIdentifier joinId, OutboxMessageIdentifier outboxMessageId, CancellationToken cancellationToken) => RequireJoinStore().IncrementFailedAsync(joinId, outboxMessageId, cancellationToken);

    private IOutboxJoinStore RequireJoinStore() => joinStore ?? throw new InvalidOperationException("SQLite outbox joins are not registered.");

    private async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, string topic, string payload, string? correlationId, DateTimeOffset? dueTimeUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(payload);
        var created = SqliteOutboxDatabase.Timestamp(timeProvider.GetUtcNow())!;
        using var command = SqliteOutboxDatabase.Command(connection, $"""
            INSERT INTO {SqliteOutboxSchema.OutboxTable}
                ("Id", "Topic", "Payload", "CorrelationId", "MessageId", "CreatedAt", "IsProcessed", "RetryCount", "DueTimeUtc", "Status")
            VALUES ($id, $topic, $payload, $correlation, $message, $created, 0, 0, $due, 0);
            """, transaction);
        SqliteOutboxDatabase.AddParameter(command, "$id", SqliteOutboxDatabase.GuidValue(Guid.NewGuid()));
        SqliteOutboxDatabase.AddParameter(command, "$topic", topic);
        SqliteOutboxDatabase.AddParameter(command, "$payload", payload);
        SqliteOutboxDatabase.AddParameter(command, "$correlation", correlationId);
        SqliteOutboxDatabase.AddParameter(command, "$message", SqliteOutboxDatabase.GuidValue(Guid.NewGuid()));
        SqliteOutboxDatabase.AddParameter(command, "$created", created);
        SqliteOutboxDatabase.AddParameter(command, "$due", SqliteOutboxDatabase.Timestamp(dueTimeUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref schemaEnsured) != 0) return;
        await SqliteOutboxSchema.EnsureAsync(factory, retry, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref schemaEnsured, 1);
    }
}
