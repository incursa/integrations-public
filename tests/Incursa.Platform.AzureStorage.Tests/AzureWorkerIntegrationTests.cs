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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Incursa.Platform.AzureStorage.Tests;

[Trait("Category", "Integration")]
public sealed class AzureWorkerIntegrationTests
{
    [Fact]
    public async Task DuplicateOutboxSignals_DoNotCauseDuplicateDispatch()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        AzuriteTestEnvironment environment = await AzuriteTestEnvironment.GetTableAsync().ConfigureAwait(false);
        AzurePlatformOptions options = AzurePlatformTestOptions.CreateIntegrationOptions(
            environment.ConnectionString,
            nameof(DuplicateOutboxSignals_DoNotCauseDuplicateDispatch),
            configure: value =>
            {
                value.EnableInboxWorker = false;
                value.EnableSchedulerWorker = false;
            });

        await AzurePlatformTestContext.ResetAsync(options).ConfigureAwait(false);

        CountingOutboxHandler handler = new("tests.outbox.duplicate-signal");
        using IHost host = CreateHost(
            options,
            services =>
            {
                services.AddSingleton(handler);
                services.AddSingleton<IOutboxHandler>(handler);
            });

        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IOutbox outbox = host.Services.GetRequiredService<IOutbox>();
            AzureOutboxService service = host.Services.GetRequiredService<AzureOutboxService>();

            await outbox.EnqueueAsync(handler.Topic, "payload", cancellationToken).ConfigureAwait(false);
            await service.Resources.SignalQueue.SendSignalAsync("outbox-ready", cancellationToken).ConfigureAwait(false);
            await service.Resources.SignalQueue.SendSignalAsync("outbox-ready", cancellationToken).ConfigureAwait(false);

            await TestWaiter.WaitUntilAsync(
                    () => handler.Count == 1,
                    TimeSpan.FromSeconds(10),
                    "The outbox worker did not dispatch the queued message.")
                .ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);

            handler.Count.ShouldBe(1);
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(false);
            await AzurePlatformTestContext.ResetAsync(options).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task InboxWorker_RetriesAndEventuallyAcknowledgesMessage()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        AzuriteTestEnvironment environment = await AzuriteTestEnvironment.GetTableAsync().ConfigureAwait(false);
        AzurePlatformOptions options = AzurePlatformTestOptions.CreateIntegrationOptions(
            environment.ConnectionString,
            nameof(InboxWorker_RetriesAndEventuallyAcknowledgesMessage),
            configure: value =>
            {
                value.EnableOutboxWorker = false;
                value.EnableSchedulerWorker = false;
                value.MaxHandlerAttempts = 3;
            });

        await AzurePlatformTestContext.ResetAsync(options).ConfigureAwait(false);

        FlakyInboxHandler handler = new("tests.inbox.retry");
        using IHost host = CreateHost(
            options,
            services =>
            {
                services.AddSingleton(handler);
                services.AddSingleton<IInboxHandler>(handler);
            });

        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IInbox inbox = host.Services.GetRequiredService<IInbox>();
            await inbox.EnqueueAsync(handler.Topic, "source-a", "msg-retry", "payload", cancellationToken).ConfigureAwait(false);

            await TestWaiter.WaitUntilAsync(
                    () => handler.Attempts >= 2,
                    TimeSpan.FromSeconds(10),
                    "The inbox worker did not retry the transiently failing message.")
                .ConfigureAwait(false);

            (await inbox.AlreadyProcessedAsync("msg-retry", "source-a", cancellationToken).ConfigureAwait(false)).ShouldBeTrue();
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(false);
            await AzurePlatformTestContext.ResetAsync(options).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task SchedulerWorker_DispatchesTimersThroughOutbox()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        AzuriteTestEnvironment environment = await AzuriteTestEnvironment.GetTableAsync().ConfigureAwait(false);
        AzurePlatformOptions options = AzurePlatformTestOptions.CreateIntegrationOptions(
            environment.ConnectionString,
            nameof(SchedulerWorker_DispatchesTimersThroughOutbox),
            configure: value =>
            {
                value.EnableInboxWorker = false;
                value.SchedulerMaxIdleDelay = TimeSpan.FromMilliseconds(50);
            });

        await AzurePlatformTestContext.ResetAsync(options).ConfigureAwait(false);

        CountingOutboxHandler handler = new("tests.scheduler.dispatch");
        using IHost host = CreateHost(
            options,
            services =>
            {
                services.AddSingleton(handler);
                services.AddSingleton<IOutboxHandler>(handler);
            });

        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ISchedulerClient scheduler = host.Services.GetRequiredService<ISchedulerClient>();
            await scheduler.ScheduleTimerAsync(handler.Topic, "payload", DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

            await TestWaiter.WaitUntilAsync(
                    () => handler.Count == 1,
                    TimeSpan.FromSeconds(10),
                    "The scheduler worker did not dispatch the timer into the outbox.")
                .ConfigureAwait(false);

            handler.Count.ShouldBe(1);
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(false);
            await AzurePlatformTestContext.ResetAsync(options).ConfigureAwait(false);
        }
    }

    private static IHost CreateHost(AzurePlatformOptions options, Action<IServiceCollection> configureServices)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configureServices);

        return new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(
                services =>
                {
                    services.AddLogging();
                    configureServices(services);
                    services.AddAzurePlatform(options);
                })
            .Build();
    }
}
