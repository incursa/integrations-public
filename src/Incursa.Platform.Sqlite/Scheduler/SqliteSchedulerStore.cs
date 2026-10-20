// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Cronos;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>SQLite scheduler store used by the local scheduler worker.</summary>
public sealed class SqliteSchedulerStore : ISchedulerStore
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes a scheduler store for one local SQLite database.</summary>
    public SqliteSchedulerStore(SqliteConnectionFactory connectionFactory, SqliteRetryPolicy retryPolicy, TimeProvider? timeProvider = null)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetNextEventTimeAsync(CancellationToken cancellationToken = default)
    {
        await SqliteSchedulerSchema.EnsureAsync(connectionFactory, retryPolicy, cancellationToken).ConfigureAwait(false);
        return await retryPolicy.ExecuteAsync<DateTimeOffset?>(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            var value = await SqliteSchedulerSchema.ScalarAsync<string>(connection, $"""
                SELECT MIN(NextDue) FROM (
                    SELECT MIN(DueTimeUtc) AS NextDue FROM {SqliteSchedulerSchema.TimersTable} WHERE Status=$ready
                    UNION ALL SELECT MIN(ScheduledTimeUtc) FROM {SqliteSchedulerSchema.JobRunsTable} WHERE Status=$ready
                    UNION ALL SELECT MIN(NextDueTimeUtc) FROM {SqliteSchedulerSchema.JobsTable} WHERE NextDueTimeUtc IS NOT NULL
                );
                """, ct, ("$ready", SqliteSchedulerSchema.Ready)).ConfigureAwait(false);
            return value is null ? null : DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CreateJobRunsFromDueJobsAsync(ISystemLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await SqliteSchedulerSchema.EnsureAsync(connectionFactory, retryPolicy, cancellationToken).ConfigureAwait(false);
        return await retryPolicy.ExecuteAsync(async ct =>
        {
            lease.ThrowIfLost();
            using var connection = connectionFactory.OpenConnection();
            await SqliteSchedulerSchema.ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                if (!await HasFencingAuthorityAsync(connection, lease.FencingToken, ct).ConfigureAwait(false))
                {
                    await SqliteSchedulerSchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
                    return 0;
                }

                var now = timeProvider.GetUtcNow();
                var due = new List<(Guid Id, string Cron)>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"SELECT Id,CronSchedule FROM {SqliteSchedulerSchema.JobsTable} WHERE NextDueTimeUtc IS NOT NULL AND NextDueTimeUtc <= $now;";
                    command.Parameters.AddWithValue("$now", SqliteSchedulerSchema.Utc(now));
                    using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false)) due.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1)));
                }
                lease.ThrowIfLost();
                foreach (var job in due)
                {
                    await SqliteSchedulerSchema.ExecuteAsync(connection, $"INSERT INTO {SqliteSchedulerSchema.JobRunsTable}(Id,JobId,ScheduledTimeUtc,Status) VALUES ($id,$job,$scheduled,$ready);", ct,
                        ("$id", Guid.NewGuid().ToString("D")), ("$job", job.Id.ToString("D")), ("$scheduled", SqliteSchedulerSchema.Utc(now)), ("$ready", SqliteSchedulerSchema.Ready)).ConfigureAwait(false);
                    var next = SqliteSchedulerClient.NextOccurrence(job.Cron, now);
                    await SqliteSchedulerSchema.ExecuteAsync(connection, $"UPDATE {SqliteSchedulerSchema.JobsTable} SET NextDueTimeUtc=$next WHERE Id=$id;", ct,
                        ("$next", next is null ? null : SqliteSchedulerSchema.Utc(next.Value)), ("$id", job.Id.ToString("D"))).ConfigureAwait(false);
                }
                await SqliteSchedulerSchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
                return due.Count;
            }
            catch { try { await SqliteSchedulerSchema.ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); } catch (SqliteException) { } throw; }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<(Guid Id, string Topic, string Payload)>> ClaimDueTimersAsync(ISystemLease lease, int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (batchSize <= 0) return Task.FromResult<IReadOnlyList<(Guid, string, string)>>(Array.Empty<(Guid, string, string)>());
        return ClaimTimersCoreAsync(lease, batchSize, cancellationToken);
    }

    private async Task<IReadOnlyList<(Guid Id, string Topic, string Payload)>> ClaimTimersCoreAsync(ISystemLease lease, int batchSize, CancellationToken cancellationToken)
    {
        await SqliteSchedulerSchema.EnsureAsync(connectionFactory, retryPolicy, cancellationToken).ConfigureAwait(false);
        return await retryPolicy.ExecuteAsync(async ct =>
        {
            lease.ThrowIfLost();
            using var connection = connectionFactory.OpenConnection();
            await SqliteSchedulerSchema.ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                if (!await HasFencingAuthorityAsync(connection, lease.FencingToken, ct).ConfigureAwait(false))
                {
                    await SqliteSchedulerSchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
                    return (IReadOnlyList<(Guid, string, string)>)Array.Empty<(Guid, string, string)>();
                }
                var now = timeProvider.GetUtcNow();
                var items = new List<(Guid, string, string)>(batchSize);
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT Id,Topic,Payload FROM {SqliteSchedulerSchema.TimersTable} WHERE Status=$ready AND DueTimeUtc <= $now AND (LockedUntilUtc IS NULL OR LockedUntilUtc <= $now) ORDER BY DueTimeUtc,CreatedAtUtc LIMIT $limit;";
                command.Parameters.AddWithValue("$ready", SqliteSchedulerSchema.Ready); command.Parameters.AddWithValue("$now", SqliteSchedulerSchema.Utc(now)); command.Parameters.AddWithValue("$limit", batchSize);
                using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false)) while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
                foreach (var item in items)
                {
                    await SqliteSchedulerSchema.ExecuteAsync(connection, $"UPDATE {SqliteSchedulerSchema.TimersTable} SET Status=$claimed,OwnerToken=$owner,LockedUntilUtc=$until WHERE Id=$id AND Status=$ready;", ct,
                        ("$claimed", SqliteSchedulerSchema.InProgress), ("$owner", lease.OwnerToken.Value.ToString()), ("$until", SqliteSchedulerSchema.Utc(now.AddSeconds(30))), ("$id", item.Item1.ToString("D")), ("$ready", SqliteSchedulerSchema.Ready)).ConfigureAwait(false);
                }
                await SqliteSchedulerSchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
                return (IReadOnlyList<(Guid, string, string)>)items;
            }
            catch { try { await SqliteSchedulerSchema.ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); } catch (SqliteException) { } throw; }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<(Guid Id, Guid JobId, string Topic, string Payload)>> ClaimDueJobRunsAsync(ISystemLease lease, int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (batchSize <= 0) return Task.FromResult<IReadOnlyList<(Guid, Guid, string, string)>>(Array.Empty<(Guid, Guid, string, string)>());
        return ClaimRunsCoreAsync(lease, batchSize, cancellationToken);
    }

    private async Task<IReadOnlyList<(Guid Id, Guid JobId, string Topic, string Payload)>> ClaimRunsCoreAsync(ISystemLease lease, int batchSize, CancellationToken cancellationToken)
    {
        await SqliteSchedulerSchema.EnsureAsync(connectionFactory, retryPolicy, cancellationToken).ConfigureAwait(false);
        return await retryPolicy.ExecuteAsync(async ct =>
        {
            lease.ThrowIfLost();
            using var connection = connectionFactory.OpenConnection();
            await SqliteSchedulerSchema.ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                if (!await HasFencingAuthorityAsync(connection, lease.FencingToken, ct).ConfigureAwait(false))
                {
                    await SqliteSchedulerSchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
                    return (IReadOnlyList<(Guid, Guid, string, string)>)Array.Empty<(Guid, Guid, string, string)>();
                }
                var now = timeProvider.GetUtcNow();
                var items = new List<(Guid, Guid, string, string)>(batchSize);
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT r.Id,r.JobId,j.Topic,COALESCE(j.Payload,'') FROM {SqliteSchedulerSchema.JobRunsTable} r JOIN {SqliteSchedulerSchema.JobsTable} j ON j.Id=r.JobId WHERE r.Status=$ready AND r.ScheduledTimeUtc <= $now AND (r.LockedUntilUtc IS NULL OR r.LockedUntilUtc <= $now) ORDER BY r.ScheduledTimeUtc,r.Id LIMIT $limit;";
                command.Parameters.AddWithValue("$ready", SqliteSchedulerSchema.Ready); command.Parameters.AddWithValue("$now", SqliteSchedulerSchema.Utc(now)); command.Parameters.AddWithValue("$limit", batchSize);
                using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false)) while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3)));
                foreach (var item in items)
                {
                    await SqliteSchedulerSchema.ExecuteAsync(connection, $"UPDATE {SqliteSchedulerSchema.JobRunsTable} SET Status=$claimed,OwnerToken=$owner,LockedUntilUtc=$until WHERE Id=$id AND Status=$ready;", ct,
                        ("$claimed", SqliteSchedulerSchema.InProgress), ("$owner", lease.OwnerToken.Value.ToString()), ("$until", SqliteSchedulerSchema.Utc(now.AddSeconds(30))), ("$id", item.Item1.ToString("D")), ("$ready", SqliteSchedulerSchema.Ready)).ConfigureAwait(false);
                }
                await SqliteSchedulerSchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
                return (IReadOnlyList<(Guid, Guid, string, string)>)items;
            }
            catch { try { await SqliteSchedulerSchema.ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); } catch (SqliteException) { } throw; }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateSchedulerStateAsync(ISystemLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await SqliteSchedulerSchema.EnsureAsync(connectionFactory, retryPolicy, cancellationToken).ConfigureAwait(false);
        await retryPolicy.ExecuteAsync(async ct =>
        {
            lease.ThrowIfLost();
            using var connection = connectionFactory.OpenConnection();
            await SqliteSchedulerSchema.ExecuteAsync(connection, $"UPDATE {SqliteSchedulerSchema.StateTable} SET CurrentFencingToken=$token,LastRunAtUtc=$now WHERE Id=1 AND CurrentFencingToken <= $token;", ct,
                ("$token", lease.FencingToken), ("$now", SqliteSchedulerSchema.Utc(timeProvider.GetUtcNow()))).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasFencingAuthorityAsync(SqliteConnection connection, long token, CancellationToken cancellationToken)
    {
        var current = await SqliteSchedulerSchema.ScalarAsync<long>(connection, $"SELECT CurrentFencingToken FROM {SqliteSchedulerSchema.StateTable} WHERE Id=1;", cancellationToken).ConfigureAwait(false);
        return token >= current;
    }
}
