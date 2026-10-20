// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Cronos;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>SQLite implementation of the durable scheduler client.</summary>
public sealed class SqliteSchedulerClient : ISchedulerClient
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes a scheduler client for one local SQLite database.</summary>
    public SqliteSchedulerClient(SqliteConnectionFactory connectionFactory, SqliteRetryPolicy retryPolicy, TimeProvider? timeProvider = null)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<string> ScheduleTimerAsync(string topic, string payload, DateTimeOffset dueTime, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        await SqliteSchedulerSchema.EnsureAsync(connectionFactory, retryPolicy, cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid();
        var now = SqliteSchedulerSchema.Utc(timeProvider.GetUtcNow());
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteSchedulerSchema.ExecuteAsync(connection,
                $"INSERT INTO {SqliteSchedulerSchema.TimersTable}(Id,Topic,Payload,DueTimeUtc,Status,CreatedAtUtc) VALUES ($id,$topic,$payload,$due,$status,$created);",
                ct, ("$id", id.ToString("D")), ("$topic", topic), ("$payload", payload), ("$due", SqliteSchedulerSchema.Utc(dueTime)), ("$status", SqliteSchedulerSchema.Ready), ("$created", now)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return id.ToString();
    }

    /// <inheritdoc />
    public async Task<bool> CancelTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(timerId, out var id)) return false;
        await SqliteSchedulerSchema.EnsureAsync(connectionFactory, retryPolicy, cancellationToken).ConfigureAwait(false);
        return await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE {SqliteSchedulerSchema.TimersTable} SET Status=$failed, LastError='Cancelled' WHERE Id=$id AND Status=$ready;";
            command.Parameters.AddWithValue("$failed", SqliteSchedulerSchema.Failed);
            command.Parameters.AddWithValue("$ready", SqliteSchedulerSchema.Ready);
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task CreateOrUpdateJobAsync(string jobName, string topic, string cronSchedule, CancellationToken cancellationToken) =>
        CreateOrUpdateJobAsync(jobName, topic, cronSchedule, null, cancellationToken);

    /// <inheritdoc />
    public async Task CreateOrUpdateJobAsync(string jobName, string topic, string cronSchedule, string? payload, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(cronSchedule);
        var next = NextOccurrence(cronSchedule, timeProvider.GetUtcNow());
        await SqliteSchedulerSchema.EnsureAsync(connectionFactory, retryPolicy, cancellationToken).ConfigureAwait(false);
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteSchedulerSchema.ExecuteAsync(connection, $"""
                INSERT INTO {SqliteSchedulerSchema.JobsTable}(Id,JobName,Topic,CronSchedule,Payload,NextDueTimeUtc)
                VALUES ($id,$name,$topic,$cron,$payload,$next)
                ON CONFLICT(JobName) DO UPDATE SET Topic=excluded.Topic,CronSchedule=excluded.CronSchedule,Payload=excluded.Payload,NextDueTimeUtc=excluded.NextDueTimeUtc;
                """, ct, ("$id", Guid.NewGuid().ToString("D")), ("$name", jobName), ("$topic", topic), ("$cron", cronSchedule), ("$payload", payload), ("$next", next is null ? null : SqliteSchedulerSchema.Utc(next.Value))).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteJobAsync(string jobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        await SqliteSchedulerSchema.EnsureAsync(connectionFactory, retryPolicy, cancellationToken).ConfigureAwait(false);
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteSchedulerSchema.ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                await SqliteSchedulerSchema.ExecuteAsync(connection, $"DELETE FROM {SqliteSchedulerSchema.JobRunsTable} WHERE JobId=(SELECT Id FROM {SqliteSchedulerSchema.JobsTable} WHERE JobName=$name);", ct, ("$name", jobName)).ConfigureAwait(false);
                await SqliteSchedulerSchema.ExecuteAsync(connection, $"DELETE FROM {SqliteSchedulerSchema.JobsTable} WHERE JobName=$name;", ct, ("$name", jobName)).ConfigureAwait(false);
                await SqliteSchedulerSchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
            }
            catch { try { await SqliteSchedulerSchema.ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); } catch (SqliteException) { } throw; }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task TriggerJobAsync(string jobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        await SqliteSchedulerSchema.EnsureAsync(connectionFactory, retryPolicy, cancellationToken).ConfigureAwait(false);
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteSchedulerSchema.ExecuteAsync(connection, $"INSERT INTO {SqliteSchedulerSchema.JobRunsTable}(Id,JobId,ScheduledTimeUtc,Status) SELECT $run,Id,$now,$ready FROM {SqliteSchedulerSchema.JobsTable} WHERE JobName=$name;", ct,
                ("$run", Guid.NewGuid().ToString("D")), ("$name", jobName), ("$now", SqliteSchedulerSchema.Utc(timeProvider.GetUtcNow())), ("$ready", SqliteSchedulerSchema.Ready)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> ClaimTimersAsync(OwnerToken ownerToken, int leaseSeconds, int batchSize, CancellationToken cancellationToken) =>
        ClaimAsync(SqliteSchedulerSchema.TimersTable, "DueTimeUtc", ownerToken, leaseSeconds, batchSize, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> ClaimJobRunsAsync(OwnerToken ownerToken, int leaseSeconds, int batchSize, CancellationToken cancellationToken) =>
        ClaimAsync(SqliteSchedulerSchema.JobRunsTable, "ScheduledTimeUtc", ownerToken, leaseSeconds, batchSize, cancellationToken);

    private async Task<IReadOnlyList<Guid>> ClaimAsync(string table, string dueColumn, OwnerToken ownerToken, int leaseSeconds, int batchSize, CancellationToken cancellationToken)
    {
        if (leaseSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        if (batchSize <= 0) return Array.Empty<Guid>();
        await SqliteSchedulerSchema.EnsureAsync(connectionFactory, retryPolicy, cancellationToken).ConfigureAwait(false);
        return await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteSchedulerSchema.ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                var now = timeProvider.GetUtcNow();
                var ids = new List<Guid>(batchSize);
                using (var select = connection.CreateCommand())
                {
                    var tieBreaker = string.Equals(table, SqliteSchedulerSchema.TimersTable, StringComparison.Ordinal) ? "CreatedAtUtc" : "Id";
                    select.CommandText = $"SELECT Id FROM {table} WHERE Status=$ready AND {dueColumn} <= $now AND (LockedUntilUtc IS NULL OR LockedUntilUtc <= $now) ORDER BY {dueColumn}, {tieBreaker} LIMIT $limit;";
                    select.Parameters.AddWithValue("$ready", SqliteSchedulerSchema.Ready);
                    select.Parameters.AddWithValue("$now", SqliteSchedulerSchema.Utc(now));
                    select.Parameters.AddWithValue("$limit", batchSize);
                    using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false)) ids.Add(Guid.Parse(reader.GetString(0)));
                }
                foreach (var id in ids)
                {
                    await SqliteSchedulerSchema.ExecuteAsync(connection, $"UPDATE {table} SET Status=$claimed,OwnerToken=$owner,LockedUntilUtc=$until WHERE Id=$id AND Status=$ready;", ct,
                        ("$claimed", SqliteSchedulerSchema.InProgress), ("$owner", ownerToken.Value.ToString()), ("$until", SqliteSchedulerSchema.Utc(now.AddSeconds(leaseSeconds))), ("$id", id.ToString("D")), ("$ready", SqliteSchedulerSchema.Ready)).ConfigureAwait(false);
                }
                await SqliteSchedulerSchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
                return (IReadOnlyList<Guid>)ids;
            }
            catch { try { await SqliteSchedulerSchema.ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); } catch (SqliteException) { } throw; }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task AckTimersAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken) => UpdateOwnedAsync(SqliteSchedulerSchema.TimersTable, ownerToken, ids, SqliteSchedulerSchema.Done, cancellationToken, true);

    /// <inheritdoc />
    public Task AckJobRunsAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken) => UpdateOwnedAsync(SqliteSchedulerSchema.JobRunsTable, ownerToken, ids, SqliteSchedulerSchema.Done, cancellationToken, true);

    /// <inheritdoc />
    public Task AbandonTimersAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken) => UpdateOwnedAsync(SqliteSchedulerSchema.TimersTable, ownerToken, ids, SqliteSchedulerSchema.Ready, cancellationToken, false);

    /// <inheritdoc />
    public Task AbandonJobRunsAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken) => UpdateOwnedAsync(SqliteSchedulerSchema.JobRunsTable, ownerToken, ids, SqliteSchedulerSchema.Ready, cancellationToken, false);

    private async Task UpdateOwnedAsync(string table, OwnerToken ownerToken, IEnumerable<Guid> ids, int status, CancellationToken cancellationToken, bool processed)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var values = ids.Distinct().ToArray();
        if (values.Length == 0) return;
        await SqliteSchedulerSchema.EnsureAsync(connectionFactory, retryPolicy, cancellationToken).ConfigureAwait(false);
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteSchedulerSchema.ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                var names = values.Select((_, i) => "$id" + i).ToArray();
                var idSql = string.Join(',', names);
                var sql = $"UPDATE {table} SET Status=$status,OwnerToken=NULL,LockedUntilUtc=NULL,RetryCount=RetryCount + $retry" + (processed ? ",ProcessedAtUtc=$processed" : "") + $" WHERE Status=$claimed AND OwnerToken=$owner AND Id IN ({idSql});";
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$status", status);
                command.Parameters.AddWithValue("$claimed", SqliteSchedulerSchema.InProgress);
                command.Parameters.AddWithValue("$owner", ownerToken.Value.ToString());
                command.Parameters.AddWithValue("$retry", processed ? 0 : 1);
                if (processed) command.Parameters.AddWithValue("$processed", SqliteSchedulerSchema.Utc(timeProvider.GetUtcNow()));
                for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue(names[i], values[i].ToString("D"));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await SqliteSchedulerSchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
            }
            catch { try { await SqliteSchedulerSchema.ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); } catch (SqliteException) { } throw; }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task ReapExpiredTimersAsync(CancellationToken cancellationToken) => ReapAsync(SqliteSchedulerSchema.TimersTable, cancellationToken);

    /// <inheritdoc />
    public Task ReapExpiredJobRunsAsync(CancellationToken cancellationToken) => ReapAsync(SqliteSchedulerSchema.JobRunsTable, cancellationToken);

    private async Task ReapAsync(string table, CancellationToken cancellationToken)
    {
        await SqliteSchedulerSchema.EnsureAsync(connectionFactory, retryPolicy, cancellationToken).ConfigureAwait(false);
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteSchedulerSchema.ExecuteAsync(connection, $"UPDATE {table} SET Status=$ready,OwnerToken=NULL,LockedUntilUtc=NULL WHERE Status=$claimed AND LockedUntilUtc IS NOT NULL AND LockedUntilUtc <= $now;", ct,
                ("$ready", SqliteSchedulerSchema.Ready), ("$claimed", SqliteSchedulerSchema.InProgress), ("$now", SqliteSchedulerSchema.Utc(timeProvider.GetUtcNow()))).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static DateTimeOffset? NextOccurrence(string cronSchedule, DateTimeOffset from)
    {
        var format = cronSchedule.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
        var expression = CronExpression.Parse(cronSchedule, format);
        var next = expression.GetNextOccurrence(from.UtcDateTime, TimeZoneInfo.Utc);
        return next.HasValue ? new DateTimeOffset(next.Value, TimeSpan.Zero) : null;
    }
}
