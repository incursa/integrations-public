// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform.Outbox;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Incursa.Platform;

/// <summary>Dispatches local SQLite outbox messages to registered handlers.</summary>
public sealed class SqliteOutboxDispatchHostedService : BackgroundService
{
    private readonly IOutboxStore store;
    private readonly IOutbox outbox;
    private readonly IReadOnlyDictionary<string, IOutboxHandler> handlers;
    private readonly SqliteWorkerOptions options;
    private readonly ILogger<SqliteOutboxDispatchHostedService> logger;

    /// <summary>Initializes a local outbox dispatch worker.</summary>
    public SqliteOutboxDispatchHostedService(
        IOutboxStore store,
        IOutbox outbox,
        IEnumerable<IOutboxHandler> handlers,
        SqlitePlatformOptions platformOptions,
        ILogger<SqliteOutboxDispatchHostedService> logger)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        ArgumentNullException.ThrowIfNull(handlers);
        this.handlers = handlers.ToDictionary(static handler => handler.Topic, StringComparer.OrdinalIgnoreCase);
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
                SqlitePlatformMetrics.RecordWorkerPass("outbox", false);
                logger.LogWarning(exception, "SQLite outbox dispatch pass failed; the next pass will retry.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task ProcessPassAsync(CancellationToken cancellationToken)
    {
        await outbox.ReapExpiredAsync(cancellationToken).ConfigureAwait(false);
        var messages = await store.ClaimDueAsync(options.BatchSize, cancellationToken).ConfigureAwait(false);
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!handlers.TryGetValue(message.Topic, out var handler))
            {
                await store.FailAsync(message.Id, $"No outbox handler is registered for topic '{message.Topic}'.", cancellationToken).ConfigureAwait(false);
                logger.LogError("Outbox message {MessageId} has no handler for topic {Topic}; it was dead-lettered.", message.MessageId, message.Topic);
                continue;
            }

            try
            {
                await handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
                await store.MarkDispatchedAsync(message.Id, cancellationToken).ConfigureAwait(false);
                SqlitePlatformMetrics.RecordWorkerPass("outbox-item", true);
            }
            catch (OutboxPermanentFailureException exception)
            {
                await store.FailAsync(message.Id, exception.Message, cancellationToken).ConfigureAwait(false);
                logger.LogWarning(exception, "Permanent outbox failure for message {MessageId}.", message.MessageId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if (message.RetryCount + 1 >= options.MaxAttempts)
                {
                    await store.FailAsync(message.Id, exception.Message, cancellationToken).ConfigureAwait(false);
                    logger.LogError(exception, "Outbox message {MessageId} exhausted its retry budget.", message.MessageId);
                }
                else
                {
                    await store.RescheduleAsync(message.Id, options.RetryDelay, exception.Message, cancellationToken).ConfigureAwait(false);
                    logger.LogWarning(exception, "Outbox message {MessageId} failed and was rescheduled.", message.MessageId);
                }
            }
        }

        SqlitePlatformMetrics.RecordWorkerPass("outbox", true);
    }
}
