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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Incursa.Platform;

/// <summary>
/// Registers the Azure Storage-backed Incursa platform provider.
/// </summary>
public static class AzurePlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Azure Storage platform provider using the supplied configuration callback.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Options callback.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAzurePlatform(
        this IServiceCollection services,
        Action<AzurePlatformOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        EnsureNotAlreadyRegistered(services);

        services.AddOptions<AzurePlatformOptions>()
            .Configure(configure);

        return AddAzurePlatformCore(services);
    }

    /// <summary>
    /// Registers the Azure Storage platform provider using a pre-built options instance.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">Platform options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAzurePlatform(
        this IServiceCollection services,
        AzurePlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        EnsureNotAlreadyRegistered(services);

        AzurePlatformOptions copy = options.Clone();
        AzurePlatformOptionsValidationHelper.ValidateAndThrow(copy, new AzurePlatformOptionsValidator());

        services.AddOptions<AzurePlatformOptions>()
            .Configure(configured => configured.CopyFrom(copy));

        return AddAzurePlatformCore(services);
    }

    private static IServiceCollection AddAzurePlatformCore(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AzurePlatformOptions>, AzurePlatformOptionsValidator>());
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<AzurePlatformOptions>>().Value.Clone());
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<AzurePlatformClientFactory>();
        services.TryAddSingleton<AzurePlatformNameResolver>();
        services.TryAddSingleton<AzurePlatformJsonSerializer>();
        services.TryAddSingleton<AzurePlatformStoreKeyRegistry>();

        services.TryAddSingleton<AzureOutboxResources>();
        services.TryAddSingleton<AzureInboxResources>();
        services.TryAddSingleton<AzureSchedulerResources>();
        services.TryAddSingleton<AzureLeaseResources>();
        services.TryAddSingleton<AzureExternalSideEffectResources>();
        services.TryAddSingleton<AzureFanoutResources>();

        services.TryAddSingleton<AzureOutboxJoinStore>();
        services.TryAddSingleton<IOutboxJoinStore>(sp => sp.GetRequiredService<AzureOutboxJoinStore>());

        services.TryAddSingleton<AzureOutboxService>();
        services.TryAddSingleton<IOutbox>(sp => sp.GetRequiredService<AzureOutboxService>());
        services.TryAddSingleton<AzureOutboxStore>();
        services.TryAddSingleton<IOutboxStore>(sp => sp.GetRequiredService<AzureOutboxStore>());
        services.TryAddSingleton<AzureOutboxStoreProvider>();
        services.TryAddSingleton<IOutboxStoreProvider>(sp => sp.GetRequiredService<AzureOutboxStoreProvider>());
        services.TryAddSingleton<AzureOutboxRouter>();
        services.TryAddSingleton<IOutboxRouter>(sp => sp.GetRequiredService<AzureOutboxRouter>());
        services.TryAddSingleton<IGlobalOutbox>(sp => new AzureGlobalOutbox(sp.GetRequiredService<IOutbox>()));
        services.TryAddSingleton<IGlobalOutboxStore>(sp => new AzureGlobalOutboxStore(sp.GetRequiredService<IOutboxStore>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOutboxHandler, JoinWaitHandler>());

        services.TryAddSingleton<AzureInboxService>();
        services.TryAddSingleton<IInbox>(sp => sp.GetRequiredService<AzureInboxService>());
        services.TryAddSingleton<AzureInboxWorkStore>();
        services.TryAddSingleton<IInboxWorkStore>(sp => sp.GetRequiredService<AzureInboxWorkStore>());
        services.TryAddSingleton<AzureInboxWorkStoreProvider>();
        services.TryAddSingleton<IInboxWorkStoreProvider>(sp => sp.GetRequiredService<AzureInboxWorkStoreProvider>());
        services.TryAddSingleton<AzureInboxRouter>();
        services.TryAddSingleton<IInboxRouter>(sp => sp.GetRequiredService<AzureInboxRouter>());
        services.TryAddSingleton<IGlobalInbox>(sp => new AzureGlobalInbox(sp.GetRequiredService<IInbox>()));
        services.TryAddSingleton<IGlobalInboxWorkStore>(sp => new AzureGlobalInboxWorkStore(sp.GetRequiredService<IInboxWorkStore>()));

        services.TryAddSingleton<AzureSchedulerCore>();
        services.TryAddSingleton<AzureSchedulerClient>();
        services.TryAddSingleton<ISchedulerClient>(sp => sp.GetRequiredService<AzureSchedulerClient>());
        services.TryAddSingleton<AzureSchedulerStore>();
        services.TryAddSingleton<ISchedulerStore>(sp => sp.GetRequiredService<AzureSchedulerStore>());
        services.TryAddSingleton<AzureSchedulerStoreProvider>();
        services.TryAddSingleton<ISchedulerStoreProvider>(sp => sp.GetRequiredService<AzureSchedulerStoreProvider>());
        services.TryAddSingleton<AzureSchedulerRouter>();
        services.TryAddSingleton<ISchedulerRouter>(sp => sp.GetRequiredService<AzureSchedulerRouter>());
        services.TryAddSingleton<IGlobalSchedulerClient>(sp => new AzureGlobalSchedulerClient(sp.GetRequiredService<ISchedulerClient>()));
        services.TryAddSingleton<IGlobalSchedulerStore>(sp => new AzureGlobalSchedulerStore(sp.GetRequiredService<ISchedulerStore>()));

        services.TryAddSingleton<AzureSystemLeaseFactory>();
        services.TryAddSingleton<ISystemLeaseFactory>(sp => sp.GetRequiredService<AzureSystemLeaseFactory>());
        services.TryAddSingleton<IGlobalSystemLeaseFactory>(sp => sp.GetRequiredService<AzureSystemLeaseFactory>());
        services.TryAddSingleton<AzureLeaseFactoryProvider>();
        services.TryAddSingleton<ILeaseFactoryProvider>(sp => sp.GetRequiredService<AzureLeaseFactoryProvider>());
        services.TryAddSingleton<AzureLeaseRouter>();
        services.TryAddSingleton<ILeaseRouter>(sp => sp.GetRequiredService<AzureLeaseRouter>());

        services.TryAddSingleton<AzureExternalSideEffectStore>();
        services.TryAddSingleton<IExternalSideEffectStore>(sp => sp.GetRequiredService<AzureExternalSideEffectStore>());
        services.TryAddSingleton<AzureExternalSideEffectStoreProvider>();
        services.TryAddSingleton<IExternalSideEffectStoreProvider>(sp => sp.GetRequiredService<AzureExternalSideEffectStoreProvider>());
        services.AddOptions<ExternalSideEffectCoordinatorOptions>();
        services.TryAddSingleton<IExternalSideEffectCoordinator, ExternalSideEffectCoordinator>();

        services.TryAddSingleton<AzureFanoutPolicyRepository>();
        services.TryAddSingleton<IFanoutPolicyRepository>(sp => sp.GetRequiredService<AzureFanoutPolicyRepository>());
        services.TryAddSingleton<AzureFanoutCursorRepository>();
        services.TryAddSingleton<IFanoutCursorRepository>(sp => sp.GetRequiredService<AzureFanoutCursorRepository>());
        services.TryAddSingleton<AzureFanoutRouter>();
        services.TryAddSingleton<IFanoutRouter>(sp => sp.GetRequiredService<AzureFanoutRouter>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AzureOutboxWorker>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AzureInboxWorker>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AzureSchedulerWorker>());

        return services;
    }

    private static void EnsureNotAlreadyRegistered(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(AzurePlatformClientFactory)))
        {
            throw new InvalidOperationException("Azure Storage platform registration has already been added.");
        }
    }
}
