// Copyright (c) Incursa
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace Incursa.Platform;

internal sealed record AzureLeaseRecordModel
{
    public string ResourceName { get; init; } = string.Empty;

    public string? OwnerToken { get; set; }

    public long FencingToken { get; set; }

    public DateTimeOffset? LeaseUntilUtc { get; set; }

    public string? ContextJson { get; set; }

    public DateTimeOffset LastUpdatedUtc { get; set; }
}

internal sealed class AzureLeaseResources
{
    public AzureLeaseResources(
        AzurePlatformClientFactory clientFactory,
        AzurePlatformOptions options,
        AzurePlatformNameResolver nameResolver,
        AzurePlatformJsonSerializer serializer,
        ILoggerFactory loggerFactory)
    {
        Table = new AzurePlatformTable(
            clientFactory,
            options,
            loggerFactory.CreateLogger<AzureLeaseResources>(),
            nameResolver.GetLeaseTableName());
        Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    public AzurePlatformTable Table { get; }

    public AzurePlatformJsonSerializer Serializer { get; }
}

internal sealed class AzureSystemLeaseFactory : ISystemLeaseFactory, IGlobalSystemLeaseFactory
{
    private readonly AzureLeaseResources resources;
    private readonly TimeProvider timeProvider;
    private readonly AzurePlatformOptions options;
    private readonly ILogger<AzureSystemLeaseFactory> logger;

    public AzureSystemLeaseFactory(
        AzureLeaseResources resources,
        TimeProvider timeProvider,
        AzurePlatformOptions options,
        ILogger<AzureSystemLeaseFactory> logger)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ISystemLease?> AcquireAsync(
        string resourceName,
        TimeSpan leaseDuration,
        string? contextJson = null,
        OwnerToken? ownerToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), leaseDuration, "Lease duration must be positive.");
        }

        await resources.Table.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        OwnerToken effectiveOwner = ownerToken ?? OwnerToken.GenerateNew();
        string rowKey = AzurePlatformRowKeys.Lease(resourceName);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            DateTimeOffset leaseUntil = now.Add(leaseDuration);
            NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                    AzurePlatformTableConstants.PartitionKey,
                    rowKey,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.HasValue)
            {
                AzureLeaseRecordModel record = new()
                {
                    ResourceName = resourceName,
                    OwnerToken = effectiveOwner.Value.ToString("N"),
                    FencingToken = 1,
                    LeaseUntilUtc = leaseUntil,
                    ContextJson = contextJson,
                    LastUpdatedUtc = now,
                };

                try
                {
                    await resources.Table.Client.AddEntityAsync(CreateEntity(rowKey, record), cancellationToken).ConfigureAwait(false);
                    return new AzureSystemLease(this, resourceName, effectiveOwner, 1, leaseDuration, options.LeaseRenewPercent, cancellationToken, logger);
                }
                catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
                {
                    logger.LogDebug(exception, "Azure lease acquire insert for resource {ResourceName} lost an optimistic concurrency race.", resourceName);
                    continue;
                }
            }

            AzureLeaseRecordModel current = Deserialize(response.Value!);
            string requestedOwner = effectiveOwner.Value.ToString("N");
            if (!string.IsNullOrWhiteSpace(current.OwnerToken) &&
                current.LeaseUntilUtc is not null &&
                current.LeaseUntilUtc > now &&
                !string.Equals(current.OwnerToken, requestedOwner, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            current.OwnerToken = requestedOwner;
            current.FencingToken += 1;
            current.LeaseUntilUtc = leaseUntil;
            current.ContextJson = contextJson;
            current.LastUpdatedUtc = now;

            try
            {
                await resources.Table.Client.UpdateEntityAsync(
                        CreateEntity(response.Value!, current),
                        response.Value!.ETag,
                        TableUpdateMode.Replace,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new AzureSystemLease(this, resourceName, effectiveOwner, current.FencingToken, leaseDuration, options.LeaseRenewPercent, cancellationToken, logger);
            }
            catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
            {
                logger.LogDebug(exception, "Azure lease acquire update for resource {ResourceName} lost an optimistic concurrency race.", resourceName);
            }
        }

        throw new InvalidOperationException($"Azure lease '{resourceName}' could not be acquired after repeated optimistic concurrency retries.");
    }

    internal async Task<(bool Renewed, long FencingToken)> TryRenewAsync(
        string resourceName,
        OwnerToken ownerToken,
        long currentFencingToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        string rowKey = AzurePlatformRowKeys.Lease(resourceName);
        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                rowKey,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return (false, currentFencingToken);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        AzureLeaseRecordModel record = Deserialize(response.Value!);
        if (!string.Equals(record.OwnerToken, ownerToken.Value.ToString("N"), StringComparison.OrdinalIgnoreCase) ||
            record.FencingToken != currentFencingToken ||
            record.LeaseUntilUtc is null ||
            record.LeaseUntilUtc <= now)
        {
            return (false, currentFencingToken);
        }

        record.FencingToken += 1;
        record.LeaseUntilUtc = now.Add(leaseDuration);
        record.LastUpdatedUtc = now;

        try
        {
            await resources.Table.Client.UpdateEntityAsync(
                    CreateEntity(response.Value!, record),
                    response.Value!.ETag,
                    TableUpdateMode.Replace,
                    cancellationToken)
                .ConfigureAwait(false);
            return (true, record.FencingToken);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception))
        {
            logger.LogDebug(exception, "Azure lease renew for resource {ResourceName} lost an optimistic concurrency race.", resourceName);
            return (false, currentFencingToken);
        }
    }

    internal async Task ReleaseAsync(string resourceName, OwnerToken ownerToken, long fencingToken, CancellationToken cancellationToken)
    {
        string rowKey = AzurePlatformRowKeys.Lease(resourceName);
        NullableResponse<TableEntity> response = await resources.Table.Client.GetEntityIfExistsAsync<TableEntity>(
                AzurePlatformTableConstants.PartitionKey,
                rowKey,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!response.HasValue)
        {
            return;
        }

        AzureLeaseRecordModel record = Deserialize(response.Value!);
        if (!string.Equals(record.OwnerToken, ownerToken.Value.ToString("N"), StringComparison.OrdinalIgnoreCase) ||
            record.FencingToken != fencingToken)
        {
            return;
        }

        record.OwnerToken = null;
        record.LeaseUntilUtc = null;
        record.ContextJson = null;
        record.LastUpdatedUtc = timeProvider.GetUtcNow();

        try
        {
            await resources.Table.Client.UpdateEntityAsync(
                    CreateEntity(response.Value!, record),
                    response.Value!.ETag,
                    TableUpdateMode.Replace,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception) || AzurePlatformExceptionHelper.IsNotFound(exception))
        {
            logger.LogDebug(exception, "Azure lease release for resource {ResourceName} lost an optimistic concurrency race.", resourceName);
        }
    }

    private TableEntity CreateEntity(string rowKey, AzureLeaseRecordModel model)
    {
        return new TableEntity(AzurePlatformTableConstants.PartitionKey, rowKey)
        {
            [AzurePlatformTableConstants.EntityTypeProperty] = "Lease",
            [AzurePlatformTableConstants.DataProperty] = resources.Serializer.SerializeToString(model),
        };
    }

    private TableEntity CreateEntity(TableEntity currentEntity, AzureLeaseRecordModel model)
    {
        return new TableEntity(currentEntity.PartitionKey, currentEntity.RowKey)
        {
            [AzurePlatformTableConstants.EntityTypeProperty] = currentEntity.GetString(AzurePlatformTableConstants.EntityTypeProperty),
            [AzurePlatformTableConstants.DataProperty] = resources.Serializer.SerializeToString(model),
        };
    }

    private AzureLeaseRecordModel Deserialize(TableEntity entity)
    {
        string json = entity.GetString(AzurePlatformTableConstants.DataProperty)
            ?? throw new InvalidOperationException($"Azure Table entity '{entity.RowKey}' does not contain serialized data.");
        return resources.Serializer.Deserialize<AzureLeaseRecordModel>(json)
            ?? throw new InvalidOperationException($"Azure Table entity '{entity.RowKey}' could not be deserialized as {nameof(AzureLeaseRecordModel)}.");
    }
}

internal sealed class AzureSystemLease : ISystemLease
{
    private readonly AzureSystemLeaseFactory factory;
    private readonly TimeSpan leaseDuration;
    private readonly ILogger logger;
    private readonly CancellationTokenSource internalCts = new();
    private readonly CancellationTokenSource linkedCts;
    private readonly Timer renewTimer;
    private readonly Lock syncRoot = new();

    private bool isDisposed;
    private bool isLost;

    public AzureSystemLease(
        AzureSystemLeaseFactory factory,
        string resourceName,
        OwnerToken ownerToken,
        long fencingToken,
        TimeSpan leaseDuration,
        double renewPercent,
        CancellationToken userCancellationToken,
        ILogger logger)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        ResourceName = resourceName ?? throw new ArgumentNullException(nameof(resourceName));
        OwnerToken = ownerToken;
        FencingToken = fencingToken;
        this.leaseDuration = leaseDuration;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        linkedCts = CancellationTokenSource.CreateLinkedTokenSource(internalCts.Token, userCancellationToken);

        TimeSpan renewAhead = TimeSpan.FromMilliseconds(leaseDuration.TotalMilliseconds * renewPercent);
        TimeSpan jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
        TimeSpan renewInterval = renewAhead + jitter;
        renewTimer = new Timer(RenewTimerCallback, null, renewInterval, renewInterval);
    }

    public string ResourceName { get; }

    public OwnerToken OwnerToken { get; }

    public long FencingToken { get; private set; }

    public CancellationToken CancellationToken => linkedCts.Token;

    public void ThrowIfLost()
    {
        if (isLost)
        {
            throw new LostLeaseException(ResourceName, OwnerToken);
        }
    }

    public async Task<bool> TryRenewNowAsync(CancellationToken cancellationToken = default)
    {
        if (isLost || isDisposed)
        {
            return false;
        }

        (bool renewed, long fencingToken) = await factory
            .TryRenewAsync(ResourceName, OwnerToken, FencingToken, leaseDuration, cancellationToken)
            .ConfigureAwait(false);

        if (!renewed)
        {
            MarkLost();
            return false;
        }

        lock (syncRoot)
        {
            FencingToken = fencingToken;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        await renewTimer.DisposeAsync().ConfigureAwait(false);
        await internalCts.CancelAsync().ConfigureAwait(false);

        if (!isLost)
        {
            try
            {
                await factory.ReleaseAsync(ResourceName, OwnerToken, FencingToken, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (ExceptionFilter.IsCatchable(exception))
            {
                logger.LogWarning(exception, "Failed to release Azure lease for resource {ResourceName}.", ResourceName);
            }
        }

        linkedCts.Dispose();
        internalCts.Dispose();
    }

    private async void RenewTimerCallback(object? state)
    {
        if (isLost || isDisposed)
        {
            return;
        }

        try
        {
            bool renewed = await TryRenewNowAsync(linkedCts.Token).ConfigureAwait(false);
            if (!renewed && !linkedCts.IsCancellationRequested && !isDisposed)
            {
                MarkLost();
            }
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested || isDisposed)
        {
        }
        catch (Exception exception) when (ExceptionFilter.IsCatchable(exception))
        {
            logger.LogWarning(exception, "Azure lease renewal failed for resource {ResourceName}. Marking the lease as lost.", ResourceName);
            MarkLost();
        }
    }

    private void MarkLost()
    {
        if (isLost)
        {
            return;
        }

        isLost = true;
        internalCts.Cancel();
    }
}
