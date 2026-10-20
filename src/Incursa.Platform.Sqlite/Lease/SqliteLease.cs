// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

internal sealed class SqliteLease : ISystemLease
{
    private readonly SqliteLeaseFactory factory;
    private readonly TimeSpan leaseDuration;
    private readonly CancellationTokenSource internalCancellation = new();
    private readonly CancellationTokenSource linkedCancellation;
    private readonly Timer renewalTimer;
    private readonly SemaphoreSlim renewalGate = new(1, 1);
    private readonly Lock sync = new();
    private volatile bool lost;
    private volatile bool disposed;
    private long fencingToken;

    internal SqliteLease(
        SqliteLeaseFactory factory,
        string resourceName,
        OwnerToken ownerToken,
        long fencingToken,
        TimeSpan leaseDuration,
        double renewPercent,
        string contextJson,
        TimeProvider timeProvider,
        CancellationToken userCancellationToken)
    {
        this.factory = factory;
        ResourceName = resourceName;
        OwnerToken = ownerToken;
        this.fencingToken = fencingToken;
        this.leaseDuration = leaseDuration;
        _ = timeProvider;
        linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(internalCancellation.Token, userCancellationToken);
        var interval = TimeSpan.FromMilliseconds(Math.Max(1d, leaseDuration.TotalMilliseconds * renewPercent));
        renewalTimer = new Timer(RenewTimerCallback, null, interval, interval);
        _ = contextJson;
    }

    public string ResourceName { get; }

    public OwnerToken OwnerToken { get; }

    public long FencingToken => Interlocked.Read(ref fencingToken);

    public CancellationToken CancellationToken => linkedCancellation.Token;

    internal TimeSpan LeaseDuration => leaseDuration;

    public void ThrowIfLost()
    {
        if (lost || linkedCancellation.IsCancellationRequested)
        {
            MarkAsLost();
            throw new LostLeaseException(ResourceName, OwnerToken);
        }
    }

    public async Task<bool> TryRenewNowAsync(CancellationToken cancellationToken = default)
    {
        if (lost || disposed)
        {
            return false;
        }

        await renewalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (lost || disposed)
            {
                return false;
            }

            var renewed = await factory.TryRenewAsync(this, cancellationToken).ConfigureAwait(false);
            if (!renewed)
            {
                MarkAsLost();
            }

            return renewed;
        }
        finally
        {
            renewalGate.Release();
        }
    }

    internal void UpdateFencingToken(long value)
    {
        lock (sync)
        {
            if (!lost && value > fencingToken)
            {
                fencingToken = value;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await renewalTimer.DisposeAsync().ConfigureAwait(false);
        await internalCancellation.CancelAsync().ConfigureAwait(false);
        if (!lost)
        {
            try { await factory.ReleaseAsync(this).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _ = ex; }
        }

        linkedCancellation.Dispose();
        internalCancellation.Dispose();
        renewalGate.Dispose();
    }

    private async void RenewTimerCallback(object? state)
    {
        if (disposed || lost || linkedCancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            if (!await TryRenewNowAsync(linkedCancellation.Token).ConfigureAwait(false))
            {
                MarkAsLost();
            }
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested || disposed)
        {
            // Shutdown is expected.
        }
        catch
        {
            MarkAsLost();
        }
    }

    private void MarkAsLost()
    {
        if (!lost)
        {
            lost = true;
            internalCancellation.Cancel();
        }
    }
}
