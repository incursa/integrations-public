// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;

namespace Incursa.Platform;

/// <summary>Creates fenced leases backed by one local SQLite database.</summary>
public sealed class SqliteLeaseFactory : ISystemLeaseFactory
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;
    private readonly SqliteLeaseOptions options;
    private readonly TimeProvider timeProvider;
    private int schemaEnsured;

    /// <summary>Initializes a SQLite lease factory.</summary>
    public SqliteLeaseFactory(
        SqliteConnectionFactory connectionFactory,
        SqliteRetryPolicy retryPolicy,
        SqliteLeaseOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        this.options = options ?? new SqliteLeaseOptions();
        this.options.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ISystemLease?> AcquireAsync(
        string resourceName,
        TimeSpan leaseDuration,
        string? contextJson = null,
        OwnerToken? ownerToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        if (leaseDuration <= TimeSpan.Zero || leaseDuration == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var token = ownerToken ?? OwnerToken.GenerateNew();
        var context = contextJson ?? JsonSerializer.Serialize(new
        {
            Host = Environment.MachineName,
            ProcessId = Environment.ProcessId,
            AcquiredAt = timeProvider.GetUtcNow(),
        });

        return await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteLeaseSchema.ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                var now = timeProvider.GetUtcNow();
                var nowText = SqliteLeaseSchema.Utc(now);
                var untilText = SqliteLeaseSchema.Utc(now.Add(leaseDuration));
                var table = SqliteIdentifier.Quote(options.TableName);

                await SqliteLeaseSchema.ExecuteAsync(connection,
                    $"INSERT OR IGNORE INTO {table}(ResourceName) VALUES ($resource);",
                    ct, ("$resource", resourceName)).ConfigureAwait(false);

                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    UPDATE {table}
                    SET OwnerToken=$owner, LeaseUntilUtc=$until, ContextJson=$context,
                        FencingToken=FencingToken+1, LastGrantedUtc=$now
                    WHERE ResourceName=$resource
                      AND (OwnerToken IS NULL OR LeaseUntilUtc IS NULL OR LeaseUntilUtc <= $now OR OwnerToken=$owner)
                    RETURNING FencingToken;
                    """;
                command.Parameters.AddWithValue("$resource", resourceName);
                command.Parameters.AddWithValue("$owner", token.Value.ToString("N"));
                command.Parameters.AddWithValue("$until", untilText);
                command.Parameters.AddWithValue("$context", context);
                command.Parameters.AddWithValue("$now", nowText);
                var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (value is null or DBNull)
                {
                    await SqliteLeaseSchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
                    return (ISystemLease?)null;
                }

                var fencingToken = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
                await SqliteLeaseSchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
                return (ISystemLease?)new SqliteLease(
                    this,
                    resourceName,
                    token,
                    fencingToken,
                    leaseDuration,
                    options.RenewPercent,
                    context,
                    timeProvider,
                    cancellationToken);
            }
            catch
            {
                try { await SqliteLeaseSchema.ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); }
                catch (Microsoft.Data.Sqlite.SqliteException) { }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> TryRenewAsync(SqliteLease lease, CancellationToken cancellationToken)
    {
        var result = await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            var now = timeProvider.GetUtcNow();
            var table = SqliteIdentifier.Quote(options.TableName);
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                UPDATE {table}
                SET LeaseUntilUtc=$until, FencingToken=FencingToken+1, LastGrantedUtc=$now
                WHERE ResourceName=$resource AND OwnerToken=$owner AND FencingToken=$fence AND LeaseUntilUtc > $now
                RETURNING FencingToken;
                """;
            command.Parameters.AddWithValue("$resource", lease.ResourceName);
            command.Parameters.AddWithValue("$owner", lease.OwnerToken.Value.ToString("N"));
            command.Parameters.AddWithValue("$fence", lease.FencingToken);
            command.Parameters.AddWithValue("$until", SqliteLeaseSchema.Utc(now.Add(lease.LeaseDuration)));
            command.Parameters.AddWithValue("$now", SqliteLeaseSchema.Utc(now));
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is not null and not DBNull ? (true, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)) : (false, 0L);
        }, cancellationToken).ConfigureAwait(false);

        if (result.Item1)
        {
            lease.UpdateFencingToken(result.Item2);
        }

        return result.Item1;
    }

    internal async Task ReleaseAsync(SqliteLease lease)
    {
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            var table = SqliteIdentifier.Quote(options.TableName);
            await SqliteLeaseSchema.ExecuteAsync(connection,
                $"UPDATE {table} SET OwnerToken=NULL, LeaseUntilUtc=NULL, ContextJson=NULL WHERE ResourceName=$resource AND OwnerToken=$owner AND FencingToken=$fence;",
                ct,
                ("$resource", lease.ResourceName),
                ("$owner", lease.OwnerToken.Value.ToString("N")),
                ("$fence", lease.FencingToken)).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (!options.EnableSchemaDeployment || Volatile.Read(ref schemaEnsured) != 0)
        {
            return;
        }

        await SqliteLeaseSchema.EnsureAsync(connectionFactory, retryPolicy, options.TableName, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref schemaEnsured, 1);
    }
}
