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

using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Incursa.Platform.AzureStorage.Tests;

[Trait("Category", "Integration")]
public sealed class AzureExternalSideEffectStoreIntegrationTests
{
    [Fact]
    public async Task TryBeginAttemptAsync_AllowsOnlyOneOwnerAcrossStoreInstances()
    {
        await using AzurePlatformTestContext context = await AzurePlatformTestContext.CreateAsync(nameof(TryBeginAttemptAsync_AllowsOnlyOneOwnerAcrossStoreInstances)).ConfigureAwait(false);
        ExternalSideEffectKey key = new("payments.capture", "effect-race");
        ExternalSideEffectRequest request = new("default", key)
        {
            CorrelationId = "corr-race",
            PayloadHash = "hash-race",
        };

        AzureExternalSideEffectStore storeA = context.ExternalSideEffectStore;
        AzureExternalSideEffectStore storeB = new(context.ExternalSideEffectResources, context.TimeProvider, NullLogger<AzureExternalSideEffectStore>.Instance);

        await storeA.GetOrCreateAsync(request, CancellationToken.None).ConfigureAwait(false);

        ExternalSideEffectAttempt attemptA = await storeA.TryBeginAttemptAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
        ExternalSideEffectAttempt attemptB = await storeB.TryBeginAttemptAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);

        attemptA.Decision.ShouldBe(ExternalSideEffectAttemptDecision.Ready);
        attemptB.Decision.ShouldBe(ExternalSideEffectAttemptDecision.Locked);
    }
}
