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

using Incursa.Platform.Outbox;
using Shouldly;

namespace Incursa.Platform.AzureStorage.Tests;

[Trait("Category", "Integration")]
public sealed class AzureOutboxJoinStoreIntegrationTests
{
    [Fact]
    public async Task AttachAndComplete_AreIdempotent()
    {
        await using AzurePlatformTestContext context = await AzurePlatformTestContext.CreateAsync(nameof(AttachAndComplete_AreIdempotent)).ConfigureAwait(false);
        OutboxJoin join = await context.OutboxJoinStore.CreateJoinAsync(tenantId: 42, expectedSteps: 1, metadata: "{\"case\":\"join\"}", CancellationToken.None).ConfigureAwait(false);
        OutboxMessageIdentifier messageId = OutboxMessageIdentifier.From(Guid.NewGuid());

        await context.OutboxJoinStore.AttachMessageToJoinAsync(join.JoinId, messageId, CancellationToken.None).ConfigureAwait(false);
        await context.OutboxJoinStore.AttachMessageToJoinAsync(join.JoinId, messageId, CancellationToken.None).ConfigureAwait(false);

        IReadOnlyList<OutboxMessageIdentifier> attached = await context.OutboxJoinStore.GetJoinMessagesAsync(join.JoinId, CancellationToken.None).ConfigureAwait(false);
        attached.Count.ShouldBe(1);

        OutboxJoin updated = await context.OutboxJoinStore.IncrementCompletedAsync(join.JoinId, messageId, CancellationToken.None).ConfigureAwait(false);
        OutboxJoin idempotent = await context.OutboxJoinStore.IncrementCompletedAsync(join.JoinId, messageId, CancellationToken.None).ConfigureAwait(false);

        updated.CompletedSteps.ShouldBe(1);
        idempotent.CompletedSteps.ShouldBe(1);
        idempotent.Status.ShouldBe((byte)1);
    }
}
