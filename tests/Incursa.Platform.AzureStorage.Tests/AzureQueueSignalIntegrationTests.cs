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

using Azure.Storage.Queues.Models;
using Shouldly;

namespace Incursa.Platform.AzureStorage.Tests;

[Trait("Category", "Integration")]
public sealed class AzureQueueSignalIntegrationTests
{
    [Fact]
    public async Task DuplicateSignals_CanBeDrainedWithoutErrors()
    {
        await using AzurePlatformTestContext context = await AzurePlatformTestContext.CreateAsync(nameof(DuplicateSignals_CanBeDrainedWithoutErrors)).ConfigureAwait(false);
        AzurePlatformQueue queue = context.OutboxResources.SignalQueue;

        await queue.SendSignalAsync("outbox-ready", CancellationToken.None).ConfigureAwait(false);
        await queue.SendSignalAsync("outbox-ready", CancellationToken.None).ConfigureAwait(false);

        (await queue.TryReceiveAndDeleteSignalAsync(CancellationToken.None).ConfigureAwait(false)).ShouldBeTrue();
        (await queue.TryReceiveAndDeleteSignalAsync(CancellationToken.None).ConfigureAwait(false)).ShouldBeTrue();
        (await queue.TryReceiveAndDeleteSignalAsync(CancellationToken.None).ConfigureAwait(false)).ShouldBeFalse();
    }

    [Fact]
    public async Task DeletedSignals_DoNotCauseSubsequentReceiveFailures()
    {
        await using AzurePlatformTestContext context = await AzurePlatformTestContext.CreateAsync(nameof(DeletedSignals_DoNotCauseSubsequentReceiveFailures)).ConfigureAwait(false);
        AzurePlatformQueue queue = context.OutboxResources.SignalQueue;
        await queue.EnsureReadyAsync(CancellationToken.None).ConfigureAwait(false);
        await queue.SendSignalAsync("outbox-ready", CancellationToken.None).ConfigureAwait(false);

        QueueMessage received = (await queue.Client.ReceiveMessagesAsync(
                maxMessages: 1,
                visibilityTimeout: TimeSpan.FromSeconds(30),
                cancellationToken: CancellationToken.None)
            .ConfigureAwait(false)).Value.Single();
        await queue.Client.DeleteMessageAsync(received.MessageId, received.PopReceipt, CancellationToken.None).ConfigureAwait(false);

        (await queue.TryReceiveAndDeleteSignalAsync(CancellationToken.None).ConfigureAwait(false)).ShouldBeFalse();
    }
}
