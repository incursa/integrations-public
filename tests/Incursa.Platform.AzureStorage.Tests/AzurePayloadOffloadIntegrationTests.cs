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

using Azure.Storage.Blobs.Models;
using Shouldly;

namespace Incursa.Platform.AzureStorage.Tests;

[Trait("Category", "Integration")]
public sealed class AzurePayloadOffloadIntegrationTests
{
    [Fact]
    public async Task OutboxPayloads_AboveThreshold_AreOffloadedToBlobs()
    {
        await using AzurePlatformTestContext context = await AzurePlatformTestContext.CreateAsync(
            nameof(OutboxPayloads_AboveThreshold_AreOffloadedToBlobs),
            configure: options => options.InlinePayloadThresholdBytes = 16).ConfigureAwait(false);

        string payload = new('x', 1024);
        await context.Outbox.EnqueueAsync("payload.offload", payload, CancellationToken.None).ConfigureAwait(false);

        IReadOnlyList<OutboxMessage> claimed = await context.OutboxStore.ClaimDueAsync(10, CancellationToken.None).ConfigureAwait(false);

        claimed.Count.ShouldBe(1);
        claimed[0].Payload.ShouldBe(payload);

        List<BlobItem> blobs = [];
        await foreach (BlobItem blob in context.GetPayloadContainerClient().GetBlobsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            blobs.Add(blob);
        }

        blobs.Count.ShouldBeGreaterThan(0);
    }
}
