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

using Incursa.Platform.Tests.TestUtilities;

namespace Incursa.Platform.AzureStorage.Tests.TestUtilities;

internal sealed class AzureSystemLeaseBehaviorHarness : ISystemLeaseBehaviorHarness
{
    private AzurePlatformTestContext? context;

    public ISystemLeaseFactory LeaseFactory => context?.LeaseFactory ?? throw new InvalidOperationException("Harness has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        context = await AzurePlatformTestContext.CreateAsync(
                nameof(AzureSystemLeaseBehaviorHarness),
                configure: options => options.LeaseRenewPercent = 2.0)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (context is not null)
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task ResetAsync() => (context ?? throw new InvalidOperationException("Harness has not been initialized.")).ResetAsync();
}
