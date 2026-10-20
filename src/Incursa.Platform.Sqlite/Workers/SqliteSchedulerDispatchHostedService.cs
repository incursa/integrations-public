// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Incursa.Platform;

/// <summary>Dispatches due SQLite timers and recurring job runs into the local outbox.</summary>
public sealed class SqliteSchedulerDispatchHostedService : BackgroundService
{
    private const string LeaseName = "sqlite:scheduler:dispatch";
    private readonly ISchedulerStore schedulerStore;
    private readonly ISchedulerClient schedulerClient;
    private readonly IOutbox outbox;
    private readonly ISystemLeaseFactory leaseFactory;
    private readonly SqliteWorkerOptions options;
    private readonly ILogger<SqliteSchedulerDispatchHostedService> logger;

    /// <summary>Initializes a local scheduler dispatch worker.</summary>
    public SqliteSchedulerDispatchHostedService(
        ISchedulerStore schedulerStore,
        ISchedulerClient schedulerClient,
        IOutbox outbox,
        ISystemLeaseFactory leaseFactory,
        SqlitePlatformOptions platformOptions,
        ILogger<SqliteSchedulerDispatchHostedService> logger)
    {
        this.schedulerStore = schedulerStore ?? throw new ArgumentNullException(nameof(schedulerStore));
        this.schedulerClient = schedulerClient ?? throw new ArgumentNullException(nameof(schedulerClient));
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        this.leaseFactory = leaseFactory ?? throw new ArgumentNullException(nameof(leaseFactory));
        ArgumentNullException.ThrowIfNull(platformOptions);
        options = platformOptions.Workers ?? throw new ArgumentNullException(nameof(platformOptions.Workers));
        options.Validate();
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollingInterval);
        do
        {
            try
            {
                await ProcessPassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                SqlitePlatformMetrics.RecordWorkerPass("scheduler", false);
                logger.LogWarning(exception, "SQLite scheduler dispatch pass failed; the next pass will retry.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task ProcessPassAsync(CancellationToken cancellationToken)
    {
        await schedulerClient.ReapExpiredTimersAsync(cancellationToken).ConfigureAwait(false);
        await schedulerClient.ReapExpiredJobRunsAsync(cancellationToken).ConfigureAwait(false);

        var lease = await leaseFactory.AcquireAsync(
            LeaseName,
            options.LeaseDuration,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return;
        }

        try
        {
            lease.ThrowIfLost();
            await schedulerStore.UpdateSchedulerStateAsync(lease, cancellationToken).ConfigureAwait(false);
            await schedulerStore.CreateJobRunsFromDueJobsAsync(lease, cancellationToken).ConfigureAwait(false);

            var timers = await schedulerStore.ClaimDueTimersAsync(lease, options.BatchSize, cancellationToken).ConfigureAwait(false);
            foreach (var timer in timers)
            {
                if (!await lease.TryRenewNowAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                lease.ThrowIfLost();
                await outbox.EnqueueAsync(timer.Topic, timer.Payload, timer.Id.ToString(), cancellationToken).ConfigureAwait(false);
                await schedulerClient.AckTimersAsync(lease.OwnerToken, [timer.Id], cancellationToken).ConfigureAwait(false);
            }

            var jobRuns = await schedulerStore.ClaimDueJobRunsAsync(lease, options.BatchSize, cancellationToken).ConfigureAwait(false);
            foreach (var run in jobRuns)
            {
                if (!await lease.TryRenewNowAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                lease.ThrowIfLost();
                await outbox.EnqueueAsync(run.Topic, run.Payload, run.Id.ToString(), cancellationToken).ConfigureAwait(false);
                await schedulerClient.AckJobRunsAsync(lease.OwnerToken, [run.Id], cancellationToken).ConfigureAwait(false);
            }

            SqlitePlatformMetrics.RecordWorkerPass("scheduler", true);
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}
