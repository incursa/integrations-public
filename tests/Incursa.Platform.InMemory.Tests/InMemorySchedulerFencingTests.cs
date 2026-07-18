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

using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Incursa.Platform.Tests;

[Trait("Category", "Unit")]
public sealed class InMemorySchedulerFencingTests
{
    /// <summary>When a scheduler observes a newer fencing token, stale leases cannot create or claim work.</summary>
    /// <intent>Verify every in-memory scheduler mutation path enforces the current fencing token and validates lease ownership.</intent>
    /// <scenario>Given due timer and recurring-job work, a current lease with token two, and a stale lease with token one.</scenario>
    /// <behavior>Then the stale lease gets no work, the current lease gets all work, and each operation checks lease validity.</behavior>
    [Fact]
    public async Task StaleFencingToken_CannotCreateOrClaimSchedulerWork()
    {
        var timeProvider = new FakeTimeProvider();
        using var provider = BuildProvider(timeProvider);
        var schedulerClient = provider.GetRequiredService<ISchedulerClient>();
        var storeProvider = provider.GetRequiredService<ISchedulerStoreProvider>();
        var schedulerStore = (await storeProvider.GetAllStoresAsync()).Single();
        var staleLease = new TrackingLease(fencingToken: 1);
        var currentLease = new TrackingLease(fencingToken: 2);

        await schedulerStore.UpdateSchedulerStateAsync(currentLease, TestContext.Current.CancellationToken);
        await schedulerClient.ScheduleTimerAsync(
            "timer.topic",
            "timer-payload",
            timeProvider.GetUtcNow(),
            TestContext.Current.CancellationToken);
        await schedulerClient.CreateOrUpdateJobAsync(
            "recurring-job",
            "job.topic",
            "* * * * * *",
            "job-payload",
            TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        (await schedulerStore.CreateJobRunsFromDueJobsAsync(staleLease, TestContext.Current.CancellationToken)).ShouldBe(0);
        (await schedulerStore.ClaimDueTimersAsync(staleLease, 10, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await schedulerStore.ClaimDueJobRunsAsync(staleLease, 10, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        staleLease.ThrowIfLostCalls.ShouldBe(3);

        (await schedulerStore.CreateJobRunsFromDueJobsAsync(currentLease, TestContext.Current.CancellationToken)).ShouldBe(1);

        var timers = await schedulerStore.ClaimDueTimersAsync(currentLease, 10, TestContext.Current.CancellationToken);
        timers.Count.ShouldBe(1);
        timers[0].Topic.ShouldBe("timer.topic");
        timers[0].Payload.ShouldBe("timer-payload");

        var jobRuns = await schedulerStore.ClaimDueJobRunsAsync(currentLease, 10, TestContext.Current.CancellationToken);
        jobRuns.Count.ShouldBe(1);
        jobRuns[0].Topic.ShouldBe("job.topic");
        jobRuns[0].Payload.ShouldBe("job-payload");
        currentLease.ThrowIfLostCalls.ShouldBe(3);
    }

    private static ServiceProvider BuildProvider(FakeTimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddInMemoryPlatformMultiDatabaseWithList(new[]
        {
            new InMemoryPlatformDatabase { Name = "default" },
        });
        services.AddTimeAbstractions(timeProvider);
        return services.BuildServiceProvider();
    }

    private sealed class TrackingLease : ISystemLease
    {
        public TrackingLease(long fencingToken)
        {
            FencingToken = fencingToken;
            OwnerToken = OwnerToken.GenerateNew();
        }

        public string ResourceName => "scheduler";

        public OwnerToken OwnerToken { get; }

        public long FencingToken { get; }

        public CancellationToken CancellationToken => CancellationToken.None;

        public int ThrowIfLostCalls { get; private set; }

        public void ThrowIfLost() => ThrowIfLostCalls++;

        public Task<bool> TryRenewNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
