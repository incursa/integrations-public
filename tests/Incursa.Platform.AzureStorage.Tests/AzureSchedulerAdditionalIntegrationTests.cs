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

using Shouldly;

namespace Incursa.Platform.AzureStorage.Tests;

[Trait("Category", "Integration")]
public sealed class AzureSchedulerAdditionalIntegrationTests
{
    [Fact]
    public async Task ScheduleTimerAsync_WithLongDelay_RemainsPendingUntilDue()
    {
        await using AzurePlatformTestContext context = await AzurePlatformTestContext.CreateAsync(nameof(ScheduleTimerAsync_WithLongDelay_RemainsPendingUntilDue)).ConfigureAwait(false);
        DateTimeOffset dueTimeUtc = DateTimeOffset.UtcNow.AddHours(6);

        await context.SchedulerClient.ScheduleTimerAsync("scheduler.long-delay", "payload", dueTimeUtc, CancellationToken.None).ConfigureAwait(false);

        IReadOnlyList<Guid> claimed = await context.SchedulerClient
            .ClaimTimersAsync(OwnerToken.GenerateNew(), leaseSeconds: 30, batchSize: 10, CancellationToken.None)
            .ConfigureAwait(false);
        DateTimeOffset? nextEventTime = await context.SchedulerStore.GetNextEventTimeAsync(CancellationToken.None).ConfigureAwait(false);

        claimed.Count.ShouldBe(0);
        nextEventTime.ShouldNotBeNull();
        nextEventTime.Value.UtcDateTime.ShouldBeInRange(
            dueTimeUtc.UtcDateTime.AddSeconds(-1),
            dueTimeUtc.UtcDateTime.AddSeconds(1));
    }
}
