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

namespace Incursa.Platform;

internal sealed class AzurePlatformStoreKeyRegistry
{
    public AzurePlatformStoreKeyRegistry(AzurePlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        CanonicalKey = $"azure-storage:{options.ResourcePrefix.Trim()}:{options.EnvironmentName.Trim()}";
    }

    public string CanonicalKey { get; }
}

internal sealed class AzureOutboxStoreProvider : IOutboxStoreProvider
{
    private readonly AzurePlatformStoreKeyRegistry keyRegistry;
    private readonly IOutboxStore store;
    private readonly IOutbox outbox;
    private readonly IReadOnlyList<IOutboxStore> stores;

    public AzureOutboxStoreProvider(
        AzurePlatformStoreKeyRegistry keyRegistry,
        IOutboxStore store,
        IOutbox outbox)
    {
        this.keyRegistry = keyRegistry ?? throw new ArgumentNullException(nameof(keyRegistry));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        stores = [store];
    }

    public Task<IReadOnlyList<IOutboxStore>> GetAllStoresAsync() => Task.FromResult(stores);

    public string GetStoreIdentifier(IOutboxStore outboxStore)
    {
        ArgumentNullException.ThrowIfNull(outboxStore);
        return keyRegistry.CanonicalKey;
    }

    public IOutboxStore? GetStoreByKey(string key) => string.IsNullOrWhiteSpace(key) ? null : store;

    public IOutbox? GetOutboxByKey(string key) => string.IsNullOrWhiteSpace(key) ? null : outbox;
}

internal sealed class AzureInboxWorkStoreProvider : IInboxWorkStoreProvider
{
    private readonly AzurePlatformStoreKeyRegistry keyRegistry;
    private readonly IInboxWorkStore store;
    private readonly IInbox inbox;
    private readonly IReadOnlyList<IInboxWorkStore> stores;

    public AzureInboxWorkStoreProvider(
        AzurePlatformStoreKeyRegistry keyRegistry,
        IInboxWorkStore store,
        IInbox inbox)
    {
        this.keyRegistry = keyRegistry ?? throw new ArgumentNullException(nameof(keyRegistry));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        stores = [store];
    }

    public Task<IReadOnlyList<IInboxWorkStore>> GetAllStoresAsync() => Task.FromResult(stores);

    public string GetStoreIdentifier(IInboxWorkStore inboxStore)
    {
        ArgumentNullException.ThrowIfNull(inboxStore);
        return keyRegistry.CanonicalKey;
    }

    public IInboxWorkStore? GetStoreByKey(string key) => string.IsNullOrWhiteSpace(key) ? null : store;

    public IInbox? GetInboxByKey(string key) => string.IsNullOrWhiteSpace(key) ? null : inbox;
}

internal sealed class AzureSchedulerStoreProvider : ISchedulerStoreProvider
{
    private readonly AzurePlatformStoreKeyRegistry keyRegistry;
    private readonly ISchedulerStore store;
    private readonly ISchedulerClient client;
    private readonly IOutbox outbox;
    private readonly IReadOnlyList<ISchedulerStore> stores;

    public AzureSchedulerStoreProvider(
        AzurePlatformStoreKeyRegistry keyRegistry,
        ISchedulerStore store,
        ISchedulerClient client,
        IOutbox outbox)
    {
        this.keyRegistry = keyRegistry ?? throw new ArgumentNullException(nameof(keyRegistry));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        stores = [store];
    }

    public Task<IReadOnlyList<ISchedulerStore>> GetAllStoresAsync() => Task.FromResult(stores);

    public string GetStoreIdentifier(ISchedulerStore schedulerStore)
    {
        ArgumentNullException.ThrowIfNull(schedulerStore);
        return keyRegistry.CanonicalKey;
    }

    public ISchedulerStore? GetStoreByKey(string key) => string.IsNullOrWhiteSpace(key) ? null : store;

    public ISchedulerClient? GetSchedulerClientByKey(string key) => string.IsNullOrWhiteSpace(key) ? null : client;

    public IOutbox? GetOutboxByKey(string key) => string.IsNullOrWhiteSpace(key) ? null : outbox;
}

internal sealed class AzureLeaseFactoryProvider : ILeaseFactoryProvider
{
    private readonly AzurePlatformStoreKeyRegistry keyRegistry;
    private readonly ISystemLeaseFactory factory;
    private readonly IReadOnlyList<ISystemLeaseFactory> factories;

    public AzureLeaseFactoryProvider(AzurePlatformStoreKeyRegistry keyRegistry, ISystemLeaseFactory factory)
    {
        this.keyRegistry = keyRegistry ?? throw new ArgumentNullException(nameof(keyRegistry));
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        factories = [factory];
    }

    public Task<IReadOnlyList<ISystemLeaseFactory>> GetAllFactoriesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(factories);

    public string GetFactoryIdentifier(ISystemLeaseFactory leaseFactory)
    {
        ArgumentNullException.ThrowIfNull(leaseFactory);
        return keyRegistry.CanonicalKey;
    }

    public Task<ISystemLeaseFactory?> GetFactoryByKeyAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.IsNullOrWhiteSpace(key) ? null : factory);
}

internal sealed class AzureExternalSideEffectStoreProvider : IExternalSideEffectStoreProvider
{
    private readonly AzurePlatformStoreKeyRegistry keyRegistry;
    private readonly IExternalSideEffectStore store;
    private readonly IReadOnlyList<IExternalSideEffectStore> stores;

    public AzureExternalSideEffectStoreProvider(
        AzurePlatformStoreKeyRegistry keyRegistry,
        IExternalSideEffectStore store)
    {
        this.keyRegistry = keyRegistry ?? throw new ArgumentNullException(nameof(keyRegistry));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        stores = [store];
    }

    public Task<IReadOnlyList<IExternalSideEffectStore>> GetAllStoresAsync() => Task.FromResult(stores);

    public string GetStoreIdentifier(IExternalSideEffectStore externalSideEffectStore)
    {
        ArgumentNullException.ThrowIfNull(externalSideEffectStore);
        return keyRegistry.CanonicalKey;
    }

    public IExternalSideEffectStore? GetStoreByKey(string key) => string.IsNullOrWhiteSpace(key) ? null : store;
}

internal sealed class AzureOutboxRouter : IOutboxRouter
{
    private readonly AzureOutboxStoreProvider provider;

    public AzureOutboxRouter(AzureOutboxStoreProvider provider)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public IOutbox GetOutbox(string routingKey) =>
        provider.GetOutboxByKey(routingKey)
        ?? throw new InvalidOperationException($"No Azure Storage outbox is registered for key '{routingKey}'.");

    public IOutbox GetOutbox(Guid routingKey) => GetOutbox(routingKey.ToString("N"));
}

internal sealed class AzureInboxRouter : IInboxRouter
{
    private readonly AzureInboxWorkStoreProvider provider;

    public AzureInboxRouter(AzureInboxWorkStoreProvider provider)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public IInbox GetInbox(string routingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
        return provider.GetInboxByKey(routingKey)
            ?? throw new InvalidOperationException($"No Azure Storage inbox is registered for key '{routingKey}'.");
    }

    public IInbox GetInbox(Guid routingKey) => GetInbox(routingKey.ToString("N"));
}

internal sealed class AzureSchedulerRouter : ISchedulerRouter
{
    private readonly AzureSchedulerStoreProvider provider;

    public AzureSchedulerRouter(AzureSchedulerStoreProvider provider)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public ISchedulerClient GetSchedulerClient(string routingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
        return provider.GetSchedulerClientByKey(routingKey)
            ?? throw new InvalidOperationException($"No Azure Storage scheduler is registered for key '{routingKey}'.");
    }

    public ISchedulerClient GetSchedulerClient(Guid routingKey) => GetSchedulerClient(routingKey.ToString("N"));
}

internal sealed class AzureLeaseRouter : ILeaseRouter
{
    private readonly AzureLeaseFactoryProvider provider;

    public AzureLeaseRouter(AzureLeaseFactoryProvider provider)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public async Task<ISystemLeaseFactory> GetLeaseFactoryAsync(string routingKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
        return await provider.GetFactoryByKeyAsync(routingKey, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No Azure Storage lease factory is registered for key '{routingKey}'.");
    }

    public async Task<ISystemLeaseFactory> GetDefaultLeaseFactoryAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ISystemLeaseFactory> factories = await provider.GetAllFactoriesAsync(cancellationToken).ConfigureAwait(false);
        return factories.Count switch
        {
            0 => throw new InvalidOperationException("No Azure Storage lease factories are configured."),
            1 => factories[0],
            _ => throw new InvalidOperationException("Multiple Azure Storage lease factories are configured. Resolve by routing key instead."),
        };
    }
}

internal sealed class AzureFanoutRouter : IFanoutRouter
{
    private readonly AzurePlatformStoreKeyRegistry keyRegistry;
    private readonly IFanoutPolicyRepository policyRepository;
    private readonly IFanoutCursorRepository cursorRepository;

    public AzureFanoutRouter(
        AzurePlatformStoreKeyRegistry keyRegistry,
        IFanoutPolicyRepository policyRepository,
        IFanoutCursorRepository cursorRepository)
    {
        this.keyRegistry = keyRegistry ?? throw new ArgumentNullException(nameof(keyRegistry));
        this.policyRepository = policyRepository ?? throw new ArgumentNullException(nameof(policyRepository));
        this.cursorRepository = cursorRepository ?? throw new ArgumentNullException(nameof(cursorRepository));
    }

    public IFanoutPolicyRepository GetPolicyRepository(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException($"No Azure Storage fanout repository is registered for key '{key}'.");
        }

        return policyRepository;
    }

    public IFanoutCursorRepository GetCursorRepository(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException($"No Azure Storage fanout repository is registered for key '{key}'.");
        }

        return cursorRepository;
    }
}
