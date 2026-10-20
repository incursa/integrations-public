// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform.Idempotency;

namespace Incursa.Platform;

/// <summary>Transactional SQLite idempotency store with reclaimable reservations.</summary>
public sealed class SqliteIdempotencyStore : IIdempotencyStore, IIdempotencyCleanupStore
{
    private const int Failed = 0;
    private const int InProgress = 1;
    private const int Completed = 2;
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;
    private readonly SqliteIdempotencyOptions options;
    private readonly TimeProvider timeProvider;
    private readonly OwnerToken ownerToken = OwnerToken.GenerateNew();
    private int schemaEnsured;

    /// <summary>Initializes a SQLite idempotency store.</summary>
    public SqliteIdempotencyStore(
        SqliteConnectionFactory connectionFactory,
        SqliteRetryPolicy retryPolicy,
        SqliteIdempotencyOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        this.options = options ?? new SqliteIdempotencyOptions();
        this.options.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<bool> TryBeginAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        return await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteIdempotencySchema.ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                var now = timeProvider.GetUtcNow();
                var lockDuration = options.LockDurationProvider?.Invoke(key) ?? options.LockDuration;
                if (lockDuration <= TimeSpan.Zero && lockDuration != Timeout.InfiniteTimeSpan)
                {
                    throw new InvalidOperationException("Lock duration must be positive or Timeout.InfiniteTimeSpan.");
                }

                var nowText = SqliteIdempotencySchema.Utc(now);
                var untilText = lockDuration == Timeout.InfiniteTimeSpan ? null : SqliteIdempotencySchema.Utc(now.Add(lockDuration));
                var table = SqliteIdentifier.Quote(options.TableName);
                using var select = connection.CreateCommand();
                select.CommandText = $"SELECT Status, LockedUntilUtc, LockedBy FROM {table} WHERE IdempotencyKey=$key;";
                select.Parameters.AddWithValue("$key", key);
                using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                    await SqliteIdempotencySchema.ExecuteAsync(connection,
                        $"INSERT INTO {table}(IdempotencyKey,Status,LockedUntilUtc,LockedBy,FailureCount,CreatedAtUtc,UpdatedAtUtc) VALUES ($key,$status,$until,$owner,0,$now,$now);",
                        ct,
                        ("$key", key), ("$status", InProgress), ("$until", untilText), ("$owner", ownerToken.Value.ToString("N")), ("$now", nowText)).ConfigureAwait(false);
                    await SqliteIdempotencySchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
                    return true;
                }

                var status = reader.GetInt32(0);
                var lockedUntil = SqliteIdempotencySchema.ParseUtc(reader.IsDBNull(1) ? null : reader.GetString(1));
                var lockedBy = reader.IsDBNull(2) ? null : reader.GetString(2);
                await reader.DisposeAsync().ConfigureAwait(false);
                if (status == Completed || (status == InProgress && !string.Equals(lockedBy, ownerToken.Value.ToString("N"), StringComparison.OrdinalIgnoreCase) && (lockedUntil is null || lockedUntil > now)))
                {
                    await SqliteIdempotencySchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
                    return false;
                }

                await SqliteIdempotencySchema.ExecuteAsync(connection,
                    $"UPDATE {table} SET Status=$status, LockedUntilUtc=$until, LockedBy=$owner, UpdatedAtUtc=$now, CompletedAtUtc=NULL WHERE IdempotencyKey=$key;",
                    ct,
                    ("$status", InProgress), ("$until", untilText), ("$owner", ownerToken.Value.ToString("N")), ("$now", nowText), ("$key", key)).ConfigureAwait(false);
                await SqliteIdempotencySchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
                return true;
            }
            catch
            {
                try { await SqliteIdempotencySchema.ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); }
                catch (Microsoft.Data.Sqlite.SqliteException) { }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CompleteAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteIdempotencySchema.ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            var now = SqliteIdempotencySchema.Utc(timeProvider.GetUtcNow());
            var table = SqliteIdentifier.Quote(options.TableName);
            try
            {
                await SqliteIdempotencySchema.ExecuteAsync(connection,
                    $"UPDATE {table} SET Status=$status, LockedUntilUtc=NULL, LockedBy=NULL, CompletedAtUtc=$now, UpdatedAtUtc=$now WHERE IdempotencyKey=$key AND Status=$inProgress AND LockedBy=$owner;",
                    ct,
                    ("$status", Completed),
                    ("$inProgress", InProgress),
                    ("$owner", ownerToken.Value.ToString("N")),
                    ("$now", now),
                    ("$key", key)).ConfigureAwait(false);

                // Preserve the provider contract that completion without a prior
                // reservation creates a completed record. INSERT OR IGNORE keeps
                // a reservation owned by another worker untouched.
                using var command = connection.CreateCommand();
                command.CommandText = $"INSERT OR IGNORE INTO {table}(IdempotencyKey,Status,FailureCount,CreatedAtUtc,UpdatedAtUtc,CompletedAtUtc) VALUES ($key,$status,0,$now,$now,$now);";
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$status", Completed);
                command.Parameters.AddWithValue("$now", now);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await SqliteIdempotencySchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
            }
            catch
            {
                try { await SqliteIdempotencySchema.ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); }
                catch (Microsoft.Data.Sqlite.SqliteException) { }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task FailAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteIdempotencySchema.ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            var now = SqliteIdempotencySchema.Utc(timeProvider.GetUtcNow());
            var table = SqliteIdentifier.Quote(options.TableName);
            try
            {
                await SqliteIdempotencySchema.ExecuteAsync(connection,
                    $"UPDATE {table} SET Status=$status, LockedUntilUtc=NULL, LockedBy=NULL, UpdatedAtUtc=$now, FailureCount=FailureCount+1 WHERE IdempotencyKey=$key AND Status=$inProgress AND LockedBy=$owner;",
                    ct,
                    ("$status", Failed),
                    ("$inProgress", InProgress),
                    ("$owner", ownerToken.Value.ToString("N")),
                    ("$now", now),
                    ("$key", key)).ConfigureAwait(false);

                // As with completion, do not overwrite a row that another
                // worker currently owns, while retaining the no-begin contract.
                using var command = connection.CreateCommand();
                command.CommandText = $"INSERT OR IGNORE INTO {table}(IdempotencyKey,Status,FailureCount,CreatedAtUtc,UpdatedAtUtc) VALUES ($key,$status,1,$now,$now);";
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$status", Failed);
                command.Parameters.AddWithValue("$now", now);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await SqliteIdempotencySchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
            }
            catch
            {
                try { await SqliteIdempotencySchema.ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); }
                catch (Microsoft.Data.Sqlite.SqliteException) { }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CleanupAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
    {
        if (retentionPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionPeriod));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        return await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            var cutoff = SqliteIdempotencySchema.Utc(timeProvider.GetUtcNow().Subtract(retentionPeriod));
            var table = SqliteIdentifier.Quote(options.TableName);
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {table} WHERE Status IN ($failed,$completed) AND COALESCE(CompletedAtUtc,UpdatedAtUtc) < $cutoff;";
            command.Parameters.AddWithValue("$failed", Failed);
            command.Parameters.AddWithValue("$completed", Completed);
            command.Parameters.AddWithValue("$cutoff", cutoff);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (!options.EnableSchemaDeployment || Volatile.Read(ref schemaEnsured) != 0)
        {
            return;
        }

        await SqliteIdempotencySchema.EnsureAsync(connectionFactory, retryPolicy, options.TableName, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref schemaEnsured, 1);
    }
}
