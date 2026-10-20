// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform.Outbox;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Incursa.Platform.Sqlite.Tests;

[Trait("Category", "Unit")]
public sealed class SqliteDispatchWorkerTests
{
    [Fact]
    public async Task OutboxWorkerDispatchesRegisteredHandlerAndAcknowledgesMessage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"incursa-worker-{Guid.NewGuid():N}.sqlite");
        var handled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOutboxHandler>(new CapturingOutboxHandler(handled));
            services.AddSqlitePlatform(path, options =>
            {
                options.WorkerInterval = TimeSpan.FromSeconds(1);
                options.Workers.PollingInterval = TimeSpan.FromMilliseconds(10);
                options.Workers.BatchSize = 4;
            });
            using var provider = services.BuildServiceProvider();
            var worker = provider.GetServices<IHostedService>().OfType<SqliteOutboxDispatchHostedService>().Single();
            await worker.StartAsync(TestContext.Current.CancellationToken);

            await provider.GetRequiredService<IOutbox>().EnqueueAsync("worker-test", "payload", TestContext.Current.CancellationToken);
            (await handled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBe("payload");

            await worker.StopAsync(TestContext.Current.CancellationToken);
            var remaining = await provider.GetRequiredService<IOutboxStore>().ClaimDueAsync(4, TestContext.Current.CancellationToken);
            remaining.ShouldBeEmpty();
        }
        finally
        {
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }
    }

    [Fact]
    public async Task SchedulerWorkerMovesDueTimerIntoOutbox()
    {
        var path = Path.Combine(Path.GetTempPath(), $"incursa-scheduler-worker-{Guid.NewGuid():N}.sqlite");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSqlitePlatform(path, options =>
            {
                options.WorkerInterval = TimeSpan.FromSeconds(1);
                options.Workers.PollingInterval = TimeSpan.FromMilliseconds(10);
                options.Workers.BatchSize = 4;
            });
            using var provider = services.BuildServiceProvider();
            var worker = provider.GetServices<IHostedService>().OfType<SqliteSchedulerDispatchHostedService>().Single();
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await provider.GetRequiredService<ISchedulerClient>().ScheduleTimerAsync(
                "scheduled-worker-test",
                "payload",
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);

            OutboxMessage? dispatched = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (dispatched is null && DateTimeOffset.UtcNow < deadline)
            {
                var pending = await provider.GetRequiredService<IOutboxStore>().ClaimDueAsync(4, TestContext.Current.CancellationToken);
                dispatched = pending.SingleOrDefault(message => string.Equals(message.Topic, "scheduled-worker-test", StringComparison.Ordinal));
                if (dispatched is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
                }
            }

            dispatched.ShouldNotBeNull();
            dispatched.Payload.ShouldBe("payload");
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }
    }

    [Fact]
    public async Task InboxWorkerDispatchesRegisteredHandlerAndAcknowledgesMessage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"incursa-inbox-worker-{Guid.NewGuid():N}.sqlite");
        var handled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IInboxHandler>(new CapturingInboxHandler(handled));
            services.AddSqlitePlatform(path, options =>
            {
                options.WorkerInterval = TimeSpan.FromSeconds(1);
                options.Workers.PollingInterval = TimeSpan.FromMilliseconds(10);
                options.Workers.BatchSize = 4;
            });
            using var provider = services.BuildServiceProvider();
            var worker = provider.GetServices<IHostedService>().OfType<SqliteInboxDispatchHostedService>().Single();
            await worker.StartAsync(TestContext.Current.CancellationToken);

            await provider.GetRequiredService<IInbox>().EnqueueAsync(
                "inbox-worker-test",
                "source",
                "message-1",
                "payload",
                TestContext.Current.CancellationToken);
            (await handled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBe("payload");

            await worker.StopAsync(TestContext.Current.CancellationToken);
            var workStore = provider.GetRequiredService<IInboxWorkStore>();
            var claimed = await workStore.ClaimAsync(OwnerToken.GenerateNew(), 30, 4, TestContext.Current.CancellationToken);
            claimed.ShouldBeEmpty();
        }
        finally
        {
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }
    }

    [Fact]
    public async Task InboxWorkerDeadLettersAfterConfiguredMaxAttempts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"incursa-inbox-max-attempts-{Guid.NewGuid():N}.sqlite");
        var attempts = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IInboxHandler>(new FailingInboxHandler(attempts));
            services.AddSqlitePlatform(path, options =>
            {
                options.EnableOutbox = false;
                options.EnableScheduling = false;
                options.EnableLeases = false;
                options.EnableIdempotency = false;
                options.EnableFanout = false;
                options.EnableOperations = false;
                options.EnableAudit = false;
                options.WorkerInterval = TimeSpan.FromSeconds(1);
                options.Workers.PollingInterval = TimeSpan.FromMilliseconds(10);
                options.Workers.RetryDelay = TimeSpan.Zero;
                options.Workers.BatchSize = 1;
                options.Inbox.MaxAttempts = 2;
            });

            using var provider = services.BuildServiceProvider();
            var worker = provider.GetServices<IHostedService>().OfType<SqliteInboxDispatchHostedService>().Single();
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await provider.GetRequiredService<IInbox>().EnqueueAsync(
                "inbox-max-attempts-test",
                "source",
                "message-max-attempts",
                "payload",
                TestContext.Current.CancellationToken);

            (await attempts.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBe(2);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            string? status = null;
            var recordedAttempts = 0;
            for (var poll = 0; poll < 500 && !string.Equals(status, "Dead", StringComparison.Ordinal); poll++)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT \"Status\", \"Attempts\" FROM \"Inbox\" WHERE \"MessageId\" = 'message-max-attempts';";
                using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                if (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    status = reader.GetString(0);
                    recordedAttempts = reader.GetInt32(1);
                }

                if (!string.Equals(status, "Dead", StringComparison.Ordinal))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
                }
            }

            await worker.StopAsync(TestContext.Current.CancellationToken);
            status.ShouldBe("Dead");
            recordedAttempts.ShouldBe(2);
        }
        finally
        {
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class CapturingOutboxHandler(TaskCompletionSource<string> completion) : IOutboxHandler
    {
        public string Topic => "worker-test";

        public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            completion.TrySetResult(message.Payload);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingInboxHandler(TaskCompletionSource<string> completion) : IInboxHandler
    {
        public string Topic => "inbox-worker-test";

        public Task HandleAsync(InboxMessage message, CancellationToken cancellationToken)
        {
            completion.TrySetResult(message.Payload);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingInboxHandler(TaskCompletionSource<int> completion) : IInboxHandler
    {
        private int invocationCount;

        public string Topic => "inbox-max-attempts-test";

        public Task HandleAsync(InboxMessage message, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref invocationCount);
            if (count >= 2)
            {
                completion.TrySetResult(count);
            }
            throw new InvalidOperationException("expected test failure");
        }
    }
}
