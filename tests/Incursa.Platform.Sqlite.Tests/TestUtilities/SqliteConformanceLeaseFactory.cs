// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform;

namespace Incursa.Platform.Sqlite.Tests.TestUtilities;

/// <summary>
/// Gives the shared lease contract tests deterministic expiration semantics. The provider's
/// production lease renewer is intentionally enabled; this adapter models a worker that stops
/// renewing at its requested lease deadline so the expiry and stale-owner cases remain stable.
/// </summary>
internal sealed class SqliteConformanceLeaseFactory : ISystemLeaseFactory
{
    private readonly ISystemLeaseFactory inner;

    public SqliteConformanceLeaseFactory(ISystemLeaseFactory inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<ISystemLease?> AcquireAsync(
        string resourceName,
        TimeSpan leaseDuration,
        string? contextJson = null,
        OwnerToken? ownerToken = null,
        CancellationToken cancellationToken = default)
    {
        var expiration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lease = await inner.AcquireAsync(resourceName, leaseDuration, contextJson, ownerToken, expiration.Token).ConfigureAwait(false);
        if (lease is null)
        {
            expiration.Dispose();
            return null;
        }

        return new Lease(lease, leaseDuration, expiration);
    }

    private sealed class Lease : ISystemLease
    {
        private readonly ISystemLease inner;
        private readonly CancellationTokenSource expiration;
        private readonly Timer timer;
        private int disposed;

        public Lease(ISystemLease inner, TimeSpan leaseDuration, CancellationTokenSource expiration)
        {
            this.inner = inner;
            this.expiration = expiration;
            timer = new Timer(Expire, null, leaseDuration, Timeout.InfiniteTimeSpan);
        }

        public string ResourceName => inner.ResourceName;

        public OwnerToken OwnerToken => inner.OwnerToken;

        public long FencingToken => inner.FencingToken;

        public CancellationToken CancellationToken => expiration.Token;

        public void ThrowIfLost()
        {
            if (expiration.IsCancellationRequested)
            {
                throw new LostLeaseException(ResourceName, OwnerToken);
            }

            inner.ThrowIfLost();
        }

        public Task<bool> TryRenewNowAsync(CancellationToken cancellationToken = default) =>
            expiration.IsCancellationRequested ? Task.FromResult(false) : inner.TryRenewNowAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            await timer.DisposeAsync().ConfigureAwait(false);
            await expiration.CancelAsync().ConfigureAwait(false);
            await inner.DisposeAsync().ConfigureAwait(false);
        }

        private async void Expire(object? state)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            await expiration.CancelAsync().ConfigureAwait(false);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
