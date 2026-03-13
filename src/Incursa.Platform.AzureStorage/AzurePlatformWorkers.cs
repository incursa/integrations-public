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

using Microsoft.Extensions.Hosting;

namespace Incursa.Platform;

internal static class AzureWorkerBackoff
{
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Jitter is used for retry dispersion, not security.")]
    public static TimeSpan Get(int attempt)
    {
        int baseMilliseconds = Math.Min(60_000, (int)(Math.Pow(2, Math.Min(10, attempt)) * 250));
        int jitterMilliseconds = Random.Shared.Next(0, 250);
        return TimeSpan.FromMilliseconds(baseMilliseconds + jitterMilliseconds);
    }
}

internal static class AzureWorkerSynchronization
{
    public static async Task<bool> WaitForSignalOrDelayAsync(
        AzurePlatformQueue signalQueue,
        TimeSpan delay,
        ILogger logger,
        string workerName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signalQueue);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerName);

        TimeSpan effectiveDelay = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(250);

        try
        {
            if (await signalQueue.TryReceiveAndDeleteSignalAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "{WorkerName} could not read its wake-up queue. Falling back to delay-based polling.", workerName);
        }

        await Task.Delay(effectiveDelay, cancellationToken).ConfigureAwait(false);
        return false;
    }
}

internal sealed class AzureOutboxWorker : BackgroundService
{
    private readonly AzureOutboxService outbox;
    private readonly AzureOutboxStore store;
    private readonly AzurePlatformOptions options;
    private readonly ILogger<AzureOutboxWorker> logger;
    private readonly IReadOnlyDictionary<string, IOutboxHandler> handlers;

    public AzureOutboxWorker(
        AzureOutboxService outbox,
        AzureOutboxStore store,
        IEnumerable<IOutboxHandler> handlers,
        AzurePlatformOptions options,
        ILogger<AzureOutboxWorker> logger)
    {
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.handlers = (handlers ?? throw new ArgumentNullException(nameof(handlers)))
            .ToDictionary(static handler => handler.Topic, StringComparer.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.EnableOutboxWorker)
        {
            logger.LogInformation("Azure Storage outbox worker is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await outbox.ReapExpiredAsync(stoppingToken).ConfigureAwait(false);
                IReadOnlyList<OutboxMessage> messages = await store.ClaimDueAsync(options.OutboxBatchSize, stoppingToken).ConfigureAwait(false);

                if (messages.Count == 0)
                {
                    await AzureWorkerSynchronization.WaitForSignalOrDelayAsync(
                            outbox.Resources.SignalQueue,
                            options.WorkerIdleDelay,
                            logger,
                            "Azure outbox worker",
                            stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }

                foreach (OutboxMessage message in messages)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    await ProcessMessageAsync(message, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Azure Storage outbox worker failed while processing a batch.");
                await Task.Delay(options.WorkerIdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Worker captures handler failures and translates them to outbox state transitions.")]
    private async Task ProcessMessageAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (!handlers.TryGetValue(message.Topic, out IOutboxHandler? handler))
        {
            await store.FailAsync(
                    message.Id,
                    $"No handler registered for topic '{message.Topic}'.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
            await store.MarkDispatchedAsync(message.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OutboxPermanentFailureException exception)
        {
            await store.FailAsync(
                    message.Id,
                    AzurePlatformFailureText.FromException(exception),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string failureText = AzurePlatformFailureText.FromException(exception);
            int nextAttempt = checked(message.RetryCount + 1);
            if (nextAttempt >= options.MaxHandlerAttempts)
            {
                await store.FailAsync(message.Id, failureText, cancellationToken).ConfigureAwait(false);
                return;
            }

            await store.RescheduleAsync(
                    message.Id,
                    AzureWorkerBackoff.Get(nextAttempt),
                    failureText,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

internal sealed class AzureInboxWorker : BackgroundService
{
    private readonly AzureInboxService inbox;
    private readonly AzureInboxWorkStore store;
    private readonly AzurePlatformOptions options;
    private readonly ILogger<AzureInboxWorker> logger;
    private readonly IReadOnlyDictionary<string, IInboxHandler> handlers;

    public AzureInboxWorker(
        AzureInboxService inbox,
        AzureInboxWorkStore store,
        IEnumerable<IInboxHandler> handlers,
        AzurePlatformOptions options,
        ILogger<AzureInboxWorker> logger)
    {
        this.inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.handlers = (handlers ?? throw new ArgumentNullException(nameof(handlers)))
            .ToDictionary(static handler => handler.Topic, StringComparer.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.EnableInboxWorker)
        {
            logger.LogInformation("Azure Storage inbox worker is disabled.");
            return;
        }

        int leaseSeconds = Math.Max(1, (int)Math.Ceiling(options.ClaimLeaseDuration.TotalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await inbox.ReapExpiredAsync(stoppingToken).ConfigureAwait(false);
                OwnerToken ownerToken = OwnerToken.GenerateNew();
                IReadOnlyList<string> messageIds = await store
                    .ClaimAsync(ownerToken, leaseSeconds, options.InboxBatchSize, stoppingToken)
                    .ConfigureAwait(false);

                if (messageIds.Count == 0)
                {
                    await AzureWorkerSynchronization.WaitForSignalOrDelayAsync(
                            inbox.Resources.SignalQueue,
                            options.WorkerIdleDelay,
                            logger,
                            "Azure inbox worker",
                            stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }

                foreach (string messageId in messageIds)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    await ProcessMessageAsync(ownerToken, messageId, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Azure Storage inbox worker failed while processing a batch.");
                await Task.Delay(options.WorkerIdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Worker captures handler failures and translates them to inbox state transitions.")]
    private async Task ProcessMessageAsync(OwnerToken ownerToken, string messageId, CancellationToken cancellationToken)
    {
        InboxMessage message = await store.GetAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (!handlers.TryGetValue(message.Topic, out IInboxHandler? handler))
        {
            await store.FailAsync(
                    ownerToken,
                    [messageId],
                    $"No handler registered for topic '{message.Topic}'.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
            await store.AckAsync(ownerToken, [messageId], cancellationToken).ConfigureAwait(false);
        }
        catch (InboxPermanentFailureException exception)
        {
            await store.FailAsync(
                    ownerToken,
                    [messageId],
                    AzurePlatformFailureText.FromException(exception),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string failureText = AzurePlatformFailureText.FromException(exception);
            if (message.Attempt >= options.MaxHandlerAttempts)
            {
                await store.FailAsync(ownerToken, [messageId], failureText, cancellationToken).ConfigureAwait(false);
                return;
            }

            await store.AbandonAsync(
                    ownerToken,
                    [messageId],
                    failureText,
                    AzureWorkerBackoff.Get(checked(message.Attempt + 1)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

internal sealed class AzureSchedulerWorker : BackgroundService
{
    private readonly AzureSchedulerCore scheduler;
    private readonly AzureOutboxService outbox;
    private readonly AzureSystemLeaseFactory leaseFactory;
    private readonly AzurePlatformOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AzureSchedulerWorker> logger;

    public AzureSchedulerWorker(
        AzureSchedulerCore scheduler,
        AzureOutboxService outbox,
        AzureSystemLeaseFactory leaseFactory,
        AzurePlatformOptions options,
        TimeProvider timeProvider,
        ILogger<AzureSchedulerWorker> logger)
    {
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        this.leaseFactory = leaseFactory ?? throw new ArgumentNullException(nameof(leaseFactory));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.EnableSchedulerWorker)
        {
            logger.LogInformation("Azure Storage scheduler worker is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ISystemLease? lease = await leaseFactory
                    .AcquireAsync(AzureSchedulerCore.LeaseResourceName, options.CoordinationLeaseDuration, cancellationToken: stoppingToken)
                    .ConfigureAwait(false);

                if (lease is null)
                {
                    await AzureWorkerSynchronization.WaitForSignalOrDelayAsync(
                            scheduler.Resources.SignalQueue,
                            options.WorkerIdleDelay,
                            logger,
                            "Azure scheduler worker",
                            stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await using (lease.ConfigureAwait(false))
                {
                    await RunLeaseLoopAsync(lease, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Azure Storage scheduler worker failed while coordinating dispatch.");
                await Task.Delay(options.WorkerIdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunLeaseLoopAsync(ISystemLease lease, CancellationToken stoppingToken)
    {
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.CancellationToken);
        CancellationToken leaseToken = linkedCts.Token;

        while (!leaseToken.IsCancellationRequested)
        {
            try
            {
                lease.ThrowIfLost();
                await scheduler.UpdateSchedulerStateAsync(lease, leaseToken).ConfigureAwait(false);
                int createdRuns = await scheduler.CreateJobRunsFromDueJobsAsync(lease, leaseToken).ConfigureAwait(false);
                int dispatched = await scheduler.RepairAndDispatchAsync(outbox, lease, options.SchedulerBatchSize, leaseToken).ConfigureAwait(false);

                if (createdRuns > 0 || dispatched > 0)
                {
                    continue;
                }

                DateTimeOffset? nextEventTime = await scheduler.GetNextEventTimeAsync(leaseToken).ConfigureAwait(false);
                TimeSpan delay = GetSchedulerDelay(nextEventTime);
                await AzureWorkerSynchronization.WaitForSignalOrDelayAsync(
                        scheduler.Resources.SignalQueue,
                        delay,
                        logger,
                        "Azure scheduler worker",
                        leaseToken)
                    .ConfigureAwait(false);
            }
            catch (LostLeaseException)
            {
                logger.LogInformation("Azure Storage scheduler worker lost its coordination lease. Reacquiring.");
                return;
            }
            catch (OperationCanceledException) when (leaseToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Azure Storage scheduler worker encountered a transient failure while holding the coordination lease.");
                await Task.Delay(options.WorkerIdleDelay, leaseToken).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan GetSchedulerDelay(DateTimeOffset? nextEventTime)
    {
        if (nextEventTime is null)
        {
            return options.SchedulerMaxIdleDelay;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        TimeSpan delay = nextEventTime.Value - now;
        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.FromMilliseconds(250);
        }

        return delay <= options.SchedulerMaxIdleDelay ? delay : options.SchedulerMaxIdleDelay;
    }
}
