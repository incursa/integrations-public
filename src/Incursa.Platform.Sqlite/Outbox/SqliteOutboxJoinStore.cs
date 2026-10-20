// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform.Outbox;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>SQLite implementation of the outbox fan-in/join store.</summary>
internal sealed class SqliteOutboxJoinStore : IOutboxJoinStore
{
    private readonly SqliteConnectionFactory factory;
    private readonly SqliteRetryPolicy retry;
    private readonly TimeProvider timeProvider;
    private int schemaEnsured;

    public SqliteOutboxJoinStore(SqliteConnectionFactory factory, SqliteRetryPolicy retry, TimeProvider? timeProvider = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.retry = retry ?? throw new ArgumentNullException(nameof(retry));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OutboxJoin> CreateJoinAsync(long tenantId, int expectedSteps, string? metadata, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSteps);
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        var joinId = JoinIdentifier.From(Guid.NewGuid());
        var now = SqliteOutboxDatabase.Timestamp(timeProvider.GetUtcNow())!;
        await retry.ExecuteAsync(async ct =>
        {
            using var connection = factory.OpenConnection();
            using var command = SqliteOutboxDatabase.Command(connection, $"""
                INSERT INTO {SqliteOutboxSchema.JoinTable}
                    ("JoinId", "PayeWaiveTenantId", "ExpectedSteps", "CompletedSteps", "FailedSteps", "Status", "CreatedUtc", "LastUpdatedUtc", "Metadata")
                VALUES ($join, $tenant, $expected, 0, 0, 0, $created, $created, $metadata);
                """);
            SqliteOutboxDatabase.AddParameter(command, "$join", SqliteOutboxDatabase.GuidValue(joinId.Value));
            SqliteOutboxDatabase.AddParameter(command, "$tenant", tenantId);
            SqliteOutboxDatabase.AddParameter(command, "$expected", expectedSteps);
            SqliteOutboxDatabase.AddParameter(command, "$created", now);
            SqliteOutboxDatabase.AddParameter(command, "$metadata", metadata);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return new OutboxJoin
        {
            JoinId = joinId,
            TenantId = tenantId,
            ExpectedSteps = expectedSteps,
            CompletedSteps = 0,
            FailedSteps = 0,
            Status = 0,
            CreatedUtc = timeProvider.GetUtcNow(),
            LastUpdatedUtc = timeProvider.GetUtcNow(),
            Metadata = metadata,
        };
    }

    public async Task AttachMessageToJoinAsync(JoinIdentifier joinId, OutboxMessageIdentifier outboxMessageId, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        await retry.ExecuteAsync(async ct =>
        {
            using var connection = factory.OpenConnection();
            using var exists = SqliteOutboxDatabase.Command(connection, $"SELECT 1 FROM {SqliteOutboxSchema.JoinTable} WHERE \"JoinId\" = $join LIMIT 1;");
            SqliteOutboxDatabase.AddParameter(exists, "$join", SqliteOutboxDatabase.GuidValue(joinId.Value));
            if (await exists.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException($"Join {joinId} not found.");
            }

            using var command = SqliteOutboxDatabase.Command(connection, $"""
                INSERT OR IGNORE INTO {SqliteOutboxSchema.JoinMemberTable}
                    ("JoinId", "OutboxMessageId", "CreatedUtc")
                VALUES ($join, $message, $created);
                """);
            SqliteOutboxDatabase.AddParameter(command, "$join", SqliteOutboxDatabase.GuidValue(joinId.Value));
            SqliteOutboxDatabase.AddParameter(command, "$message", SqliteOutboxDatabase.GuidValue(outboxMessageId.Value));
            SqliteOutboxDatabase.AddParameter(command, "$created", SqliteOutboxDatabase.Timestamp(timeProvider.GetUtcNow())!);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OutboxJoin?> GetJoinAsync(JoinIdentifier joinId, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return await retry.ExecuteAsync(async ct =>
        {
            using var connection = factory.OpenConnection();
            using var command = SqliteOutboxDatabase.Command(connection, $"SELECT \"JoinId\", \"PayeWaiveTenantId\", \"ExpectedSteps\", \"CompletedSteps\", \"FailedSteps\", \"Status\", \"CreatedUtc\", \"LastUpdatedUtc\", \"Metadata\" FROM {SqliteOutboxSchema.JoinTable} WHERE \"JoinId\" = $join;");
            SqliteOutboxDatabase.AddParameter(command, "$join", SqliteOutboxDatabase.GuidValue(joinId.Value));
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadJoin(reader) : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<OutboxJoin> IncrementCompletedAsync(JoinIdentifier joinId, OutboxMessageIdentifier outboxMessageId, CancellationToken cancellationToken) => IncrementAsync(joinId, outboxMessageId, completed: true, cancellationToken);

    public Task<OutboxJoin> IncrementFailedAsync(JoinIdentifier joinId, OutboxMessageIdentifier outboxMessageId, CancellationToken cancellationToken) => IncrementAsync(joinId, outboxMessageId, completed: false, cancellationToken);

    private async Task<OutboxJoin> IncrementAsync(JoinIdentifier joinId, OutboxMessageIdentifier outboxMessageId, bool completed, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return await retry.ExecuteAsync(async ct =>
        {
            using var connection = factory.OpenConnection();
            await SqliteOutboxDatabase.BeginImmediateAsync(connection, ct).ConfigureAwait(false);
            try
            {
                var current = await ReadJoinAsync(connection, joinId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Join {joinId} not found.");
                using var member = SqliteOutboxDatabase.Command(connection, $"SELECT \"CompletedAt\", \"FailedAt\" FROM {SqliteOutboxSchema.JoinMemberTable} WHERE \"JoinId\" = $join AND \"OutboxMessageId\" = $message;");
                SqliteOutboxDatabase.AddParameter(member, "$join", SqliteOutboxDatabase.GuidValue(joinId.Value));
                SqliteOutboxDatabase.AddParameter(member, "$message", SqliteOutboxDatabase.GuidValue(outboxMessageId.Value));
                bool memberExists;
                bool memberAlreadyCompleted;
                using (var reader = await member.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    memberExists = await reader.ReadAsync(ct).ConfigureAwait(false);
                    memberAlreadyCompleted = memberExists && (!reader.IsDBNull(0) || !reader.IsDBNull(1));
                }

                if (!memberExists || memberAlreadyCompleted)
                {
                    await SqliteOutboxDatabase.CommitAsync(connection, ct).ConfigureAwait(false);
                    return current;
                }

                var now = SqliteOutboxDatabase.Timestamp(timeProvider.GetUtcNow())!;
                var memberColumn = completed ? "CompletedAt" : "FailedAt";
                using (var updateMember = SqliteOutboxDatabase.Command(connection, $"UPDATE {SqliteOutboxSchema.JoinMemberTable} SET \"{memberColumn}\" = $now WHERE \"JoinId\" = $join AND \"OutboxMessageId\" = $message AND \"CompletedAt\" IS NULL AND \"FailedAt\" IS NULL;"))
                {
                    SqliteOutboxDatabase.AddParameter(updateMember, "$now", now);
                    SqliteOutboxDatabase.AddParameter(updateMember, "$join", SqliteOutboxDatabase.GuidValue(joinId.Value));
                    SqliteOutboxDatabase.AddParameter(updateMember, "$message", SqliteOutboxDatabase.GuidValue(outboxMessageId.Value));
                    await updateMember.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                var counterColumn = completed ? "CompletedSteps" : "FailedSteps";
                using (var updateJoin = SqliteOutboxDatabase.Command(connection, $"UPDATE {SqliteOutboxSchema.JoinTable} SET \"{counterColumn}\" = \"{counterColumn}\" + 1, \"LastUpdatedUtc\" = $now WHERE \"JoinId\" = $join AND (\"CompletedSteps\" + \"FailedSteps\") < \"ExpectedSteps\";"))
                {
                    SqliteOutboxDatabase.AddParameter(updateJoin, "$now", now);
                    SqliteOutboxDatabase.AddParameter(updateJoin, "$join", SqliteOutboxDatabase.GuidValue(joinId.Value));
                    await updateJoin.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                var result = await ReadJoinAsync(connection, joinId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Join {joinId} disappeared during update.");
                await SqliteOutboxDatabase.CommitAsync(connection, ct).ConfigureAwait(false);
                return result;
            }
            catch
            {
                try { await SqliteOutboxDatabase.RollbackAsync(connection).ConfigureAwait(false); }
                catch (SqliteException) { }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateStatusAsync(JoinIdentifier joinId, byte status, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        await retry.ExecuteAsync(async ct =>
        {
            using var connection = factory.OpenConnection();
            using var command = SqliteOutboxDatabase.Command(connection, $"UPDATE {SqliteOutboxSchema.JoinTable} SET \"Status\" = $status, \"LastUpdatedUtc\" = $now WHERE \"JoinId\" = $join;");
            SqliteOutboxDatabase.AddParameter(command, "$status", status);
            SqliteOutboxDatabase.AddParameter(command, "$now", SqliteOutboxDatabase.Timestamp(timeProvider.GetUtcNow())!);
            SqliteOutboxDatabase.AddParameter(command, "$join", SqliteOutboxDatabase.GuidValue(joinId.Value));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutboxMessageIdentifier>> GetJoinMessagesAsync(JoinIdentifier joinId, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return await retry.ExecuteAsync(async ct =>
        {
            using var connection = factory.OpenConnection();
            using var command = SqliteOutboxDatabase.Command(connection, $"SELECT \"OutboxMessageId\" FROM {SqliteOutboxSchema.JoinMemberTable} WHERE \"JoinId\" = $join ORDER BY \"CreatedUtc\";");
            SqliteOutboxDatabase.AddParameter(command, "$join", SqliteOutboxDatabase.GuidValue(joinId.Value));
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<OutboxMessageIdentifier>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(SqliteOutboxDatabase.Message(reader.GetValue(0)));
            return (IReadOnlyList<OutboxMessageIdentifier>)result;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref schemaEnsured) != 0) return;
        await SqliteOutboxSchema.EnsureAsync(factory, retry, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref schemaEnsured, 1);
    }

    private async Task<OutboxJoin?> ReadJoinAsync(SqliteConnection connection, JoinIdentifier joinId, CancellationToken cancellationToken)
    {
        using var command = SqliteOutboxDatabase.Command(connection, $"SELECT \"JoinId\", \"PayeWaiveTenantId\", \"ExpectedSteps\", \"CompletedSteps\", \"FailedSteps\", \"Status\", \"CreatedUtc\", \"LastUpdatedUtc\", \"Metadata\" FROM {SqliteOutboxSchema.JoinTable} WHERE \"JoinId\" = $join;");
        SqliteOutboxDatabase.AddParameter(command, "$join", SqliteOutboxDatabase.GuidValue(joinId.Value));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJoin(reader) : null;
    }

    private static OutboxJoin ReadJoin(SqliteDataReader reader) => new()
    {
        JoinId = SqliteOutboxDatabase.Join(reader.GetValue(0)),
        TenantId = reader.GetInt64(1),
        ExpectedSteps = reader.GetInt32(2),
        CompletedSteps = reader.GetInt32(3),
        FailedSteps = reader.GetInt32(4),
        Status = Convert.ToByte(reader.GetValue(5), System.Globalization.CultureInfo.InvariantCulture),
        CreatedUtc = SqliteOutboxDatabase.TimestampValue(reader.GetValue(6)),
        LastUpdatedUtc = SqliteOutboxDatabase.TimestampValue(reader.GetValue(7)),
        Metadata = reader.IsDBNull(8) ? null : reader.GetString(8),
    };
}
