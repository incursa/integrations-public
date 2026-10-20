// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

#pragma warning disable MA0048 // The two small repositories share the SQLite fanout persistence helpers.

using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>SQLite implementation of the fanout cadence policy repository.</summary>
public sealed class SqliteFanoutPolicyRepository : IFanoutPolicyRepository
{
    private const int DefaultEverySeconds = 300;
    private const int DefaultJitterSeconds = 60;

    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes a repository backed by the configured local SQLite file.</summary>
    public SqliteFanoutPolicyRepository(
        SqliteConnectionFactory connectionFactory,
        SqliteRetryPolicy retryPolicy,
        TimeProvider? timeProvider = null)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<(int everySeconds, int jitterSeconds)> GetCadenceAsync(
        string fanoutTopic,
        string workKey,
        CancellationToken ct)
    {
        ValidateKey(fanoutTopic, nameof(fanoutTopic));
        ValidateKey(workKey, nameof(workKey));
        await SqliteFanoutSchema.EnsureAsync(connectionFactory, retryPolicy, ct).ConfigureAwait(false);

        return await retryPolicy.ExecuteAsync<(int everySeconds, int jitterSeconds)>(async cancellationToken =>
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT DefaultEverySeconds, JitterSeconds FROM {SqliteFanoutSchema.PolicyTable} WHERE FanoutTopic=$topic AND WorkKey=$work;";
            command.Parameters.AddWithValue("$topic", fanoutTopic);
            command.Parameters.AddWithValue("$work", workKey);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return (DefaultEverySeconds, DefaultJitterSeconds);
            }

            return (reader.GetInt32(0), reader.GetInt32(1));
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetCadenceAsync(
        string fanoutTopic,
        string workKey,
        int everySeconds,
        int jitterSeconds,
        CancellationToken ct)
    {
        ValidateKey(fanoutTopic, nameof(fanoutTopic));
        ValidateKey(workKey, nameof(workKey));
        if (everySeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(everySeconds), everySeconds, "Cadence must be positive.");
        }

        if (jitterSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterSeconds), jitterSeconds, "Jitter must be non-negative.");
        }

        await SqliteFanoutSchema.EnsureAsync(connectionFactory, retryPolicy, ct).ConfigureAwait(false);
        await retryPolicy.ExecuteAsync(async cancellationToken =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteFanoutSchema.ExecuteAsync(connection, $"""
                INSERT INTO {SqliteFanoutSchema.PolicyTable}
                    (FanoutTopic, WorkKey, DefaultEverySeconds, JitterSeconds, UpdatedAtUtc)
                VALUES ($topic, $work, $every, $jitter, $updated)
                ON CONFLICT (FanoutTopic, WorkKey) DO UPDATE SET
                    DefaultEverySeconds=excluded.DefaultEverySeconds,
                    JitterSeconds=excluded.JitterSeconds,
                    UpdatedAtUtc=excluded.UpdatedAtUtc;
                """, cancellationToken,
                ("$topic", fanoutTopic),
                ("$work", workKey),
                ("$every", everySeconds),
                ("$jitter", jitterSeconds),
                ("$updated", SqliteFanoutSchema.Utc(timeProvider.GetUtcNow()))).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private static void ValidateKey(string value, string parameterName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
}

/// <summary>SQLite implementation of the fanout completion cursor repository.</summary>
public sealed class SqliteFanoutCursorRepository : IFanoutCursorRepository
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes a repository backed by the configured local SQLite file.</summary>
    public SqliteFanoutCursorRepository(
        SqliteConnectionFactory connectionFactory,
        SqliteRetryPolicy retryPolicy,
        TimeProvider? timeProvider = null)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetLastAsync(
        string fanoutTopic,
        string workKey,
        string shardKey,
        CancellationToken ct)
    {
        ValidateKey(fanoutTopic, nameof(fanoutTopic));
        ValidateKey(workKey, nameof(workKey));
        ValidateKey(shardKey, nameof(shardKey));
        await SqliteFanoutSchema.EnsureAsync(connectionFactory, retryPolicy, ct).ConfigureAwait(false);

        return await retryPolicy.ExecuteAsync<DateTimeOffset?>(async cancellationToken =>
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT LastCompletedAtUtc FROM {SqliteFanoutSchema.CursorTable} WHERE FanoutTopic=$topic AND WorkKey=$work AND ShardKey=$shard;";
            command.Parameters.AddWithValue("$topic", fanoutTopic);
            command.Parameters.AddWithValue("$work", workKey);
            command.Parameters.AddWithValue("$shard", shardKey);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is null or DBNull ? null : SqliteFanoutSchema.ReadUtc(Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture)!);
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkCompletedAsync(
        string fanoutTopic,
        string workKey,
        string shardKey,
        DateTimeOffset completedAt,
        CancellationToken ct)
    {
        ValidateKey(fanoutTopic, nameof(fanoutTopic));
        ValidateKey(workKey, nameof(workKey));
        ValidateKey(shardKey, nameof(shardKey));
        await SqliteFanoutSchema.EnsureAsync(connectionFactory, retryPolicy, ct).ConfigureAwait(false);

        var completed = SqliteFanoutSchema.Utc(completedAt);
        await retryPolicy.ExecuteAsync(async cancellationToken =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteFanoutSchema.ExecuteAsync(connection, $"""
                INSERT INTO {SqliteFanoutSchema.CursorTable}
                    (FanoutTopic, WorkKey, ShardKey, LastCompletedAtUtc, UpdatedAtUtc)
                VALUES ($topic, $work, $shard, $completed, $updated)
                ON CONFLICT (FanoutTopic, WorkKey, ShardKey) DO UPDATE SET
                    LastCompletedAtUtc=excluded.LastCompletedAtUtc,
                    UpdatedAtUtc=excluded.UpdatedAtUtc
                WHERE excluded.LastCompletedAtUtc > {SqliteFanoutSchema.CursorTable}.LastCompletedAtUtc;
                """, cancellationToken,
                ("$topic", fanoutTopic),
                ("$work", workKey),
                ("$shard", shardKey),
                ("$completed", completed),
                ("$updated", SqliteFanoutSchema.Utc(timeProvider.GetUtcNow()))).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private static void ValidateKey(string value, string parameterName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
}
