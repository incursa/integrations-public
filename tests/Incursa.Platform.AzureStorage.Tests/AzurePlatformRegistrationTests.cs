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
using Shouldly;

namespace Incursa.Platform.AzureStorage.Tests;

[Trait("Category", "Unit")]
public sealed class AzurePlatformRegistrationTests
{
    [Fact]
    public void AddAzurePlatform_RegistersPublicPlatformServices()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAzurePlatform(AzurePlatformTestOptions.CreateUnitOptions());

        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        provider.GetRequiredService<IOutbox>().ShouldNotBeNull();
        provider.GetRequiredService<IOutboxStoreProvider>().ShouldNotBeNull();
        provider.GetRequiredService<IOutboxRouter>().GetOutbox(Guid.NewGuid()).ShouldNotBeNull();
        provider.GetRequiredService<IInbox>().ShouldNotBeNull();
        provider.GetRequiredService<IInboxWorkStoreProvider>().ShouldNotBeNull();
        provider.GetRequiredService<IInboxRouter>().GetInbox(Guid.NewGuid()).ShouldNotBeNull();
        provider.GetRequiredService<ISchedulerClient>().ShouldNotBeNull();
        provider.GetRequiredService<ISchedulerStoreProvider>().ShouldNotBeNull();
        provider.GetRequiredService<ISchedulerRouter>().GetSchedulerClient(Guid.NewGuid()).ShouldNotBeNull();
        provider.GetRequiredService<ISystemLeaseFactory>().ShouldNotBeNull();
        provider.GetRequiredService<ILeaseRouter>().ShouldNotBeNull();
        provider.GetRequiredService<IExternalSideEffectStoreProvider>().ShouldNotBeNull();
        provider.GetRequiredService<IFanoutRouter>().GetCursorRepository("tenant-a").ShouldNotBeNull();
        provider.GetRequiredService<IGlobalOutbox>().ShouldNotBeNull();
        provider.GetRequiredService<IGlobalInbox>().ShouldNotBeNull();
        provider.GetRequiredService<IGlobalSchedulerClient>().ShouldNotBeNull();
        provider.GetRequiredService<IGlobalSystemLeaseFactory>().ShouldNotBeNull();
        provider.GetRequiredService<IExternalSideEffectCoordinator>().ShouldNotBeNull();
    }

    [Fact]
    public async Task TransactionBoundOutboxOverloads_ThrowNotSupportedException()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAzurePlatform(AzurePlatformTestOptions.CreateUnitOptions());

        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        IOutbox outbox = provider.GetRequiredService<IOutbox>();

        await Should.ThrowAsync<NotSupportedException>(() =>
            outbox.EnqueueAsync("topic", "payload", new TestDbTransaction(), CancellationToken.None));
    }
}
