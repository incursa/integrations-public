// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Incursa.Platform;

/// <summary>Dispatches local SQLite inbox messages to registered handlers.</summary>
public sealed class SqliteInboxDispatchHostedService : BackgroundService
{
    private readonly IInboxWorkStore store;
    private readonly IReadOnlyDictionary<string, IInboxHandler> handlers;
    private readonly SqliteWorkerOptions workerOptions;
    private readonly SqliteInboxOptions inboxOptions;
    private readonly OwnerToken ownerToken = OwnerToken.GenerateNew();
    private readonly ILogger<SqliteInboxDispatchHostedService> logger;

    /// <summary>Initializes a local inbox dispatch worker.</summary>
    public SqliteInboxDispatchHostedService(
        IInboxWorkStore store,
        IEnumerable<IInboxHandler> handlers,
        SqlitePlatformOptions platformOptions,
        SqliteInboxOptions inboxOptions,
        ILogger<SqliteInboxDispatchHostedService> logger)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(handlers);
        this.handlers = handlers.ToDictionary(static handler => handler.Topic, StringComparer.OrdinalIgnoreCase);
        ArgumentNullException.ThrowIfNull(platformOptions);
        workerOptions = platformOptions.Workers ?? throw new ArgumentNullException(nameof(platformOptions.Workers));
        workerOptions.Validate();
        this.inboxOptions = inboxOptions ?? throw new ArgumentNullException(nameof(inboxOptions));
        this.inboxOptions.Validate();
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(workerOptions.PollingInterval);
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
                SqlitePlatformMetrics.RecordWorkerPass("inbox", false);
                logger.LogWarning(exception, "SQLite inbox dispatch pass failed; the next pass will retry.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task ProcessPassAsync(CancellationToken cancellationToken)
    {
        await store.ReapExpiredAsync(cancellationToken).ConfigureAwait(false);
        var messageIds = await store.ClaimAsync(
            ownerToken,
            Math.Max(1, (int)Math.Ceiling(workerOptions.LeaseDuration.TotalSeconds)),
            workerOptions.BatchSize,
            cancellationToken).ConfigureAwait(false);

        foreach (var messageId in messageIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = await store.GetAsync(messageId, cancellationToken).ConfigureAwait(false);
            // Attempts is the one-based processing-attempt number persisted by the
            // inbox store.  Do not invoke a handler for a row that was recovered
            // after its budget was exhausted (for example, a row written by an
            // earlier provider version).
            if (message.Attempt > inboxOptions.MaxAttempts)
            {
                await store.FailAsync(ownerToken, [messageId], "Inbox message exceeded its retry budget.", cancellationToken).ConfigureAwait(false);
                logger.LogError("Inbox message {MessageId} exceeded its retry budget before dispatch.", messageId);
                continue;
            }

            if (!handlers.TryGetValue(message.Topic, out var handler))
            {
                await store.FailAsync(ownerToken, [messageId], $"No inbox handler is registered for topic '{message.Topic}'.", cancellationToken).ConfigureAwait(false);
                logger.LogError("Inbox message {MessageId} has no handler for topic {Topic}; it was dead-lettered.", messageId, message.Topic);
                continue;
            }

            try
            {
                await handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
                await store.AckAsync(ownerToken, [messageId], cancellationToken).ConfigureAwait(false);
                SqlitePlatformMetrics.RecordWorkerPass("inbox-item", true);
            }
            catch (InboxPermanentFailureException exception)
            {
                await store.FailAsync(ownerToken, [messageId], exception.Message, cancellationToken).ConfigureAwait(false);
                logger.LogWarning(exception, "Permanent inbox failure for message {MessageId}.", messageId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // AbandonAsync increments Attempts in the same owner-checked
                // transaction.  A message at the maximum has therefore just
                // failed its final permitted handler invocation.
                if (message.Attempt >= inboxOptions.MaxAttempts)
                {
                    await store.FailAsync(ownerToken, [messageId], exception.Message, cancellationToken).ConfigureAwait(false);
                    logger.LogError(exception, "Inbox message {MessageId} exhausted its retry budget.", messageId);
                }
                else
                {
                    await store.AbandonAsync(ownerToken, [messageId], exception.Message, workerOptions.RetryDelay, cancellationToken).ConfigureAwait(false);
                    logger.LogWarning(exception, "Inbox message {MessageId} failed and was rescheduled.", messageId);
                }
            }
        }

        SqlitePlatformMetrics.RecordWorkerPass("inbox", true);
    }
}
