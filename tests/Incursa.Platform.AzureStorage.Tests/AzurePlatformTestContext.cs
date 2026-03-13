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

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging.Abstractions;

namespace Incursa.Platform.AzureStorage.Tests;

internal sealed class AzurePlatformTestContext : IAsyncDisposable
{
    private readonly NullLoggerFactory loggerFactory = NullLoggerFactory.Instance;

    private AzurePlatformTestContext(AzurePlatformOptions options, TimeProvider timeProvider)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        ClientFactory = new AzurePlatformClientFactory(options);
        NameResolver = new AzurePlatformNameResolver(options);
        Serializer = new AzurePlatformJsonSerializer(options);

        OutboxResources = new AzureOutboxResources(ClientFactory, options, NameResolver, Serializer, loggerFactory);
        InboxResources = new AzureInboxResources(ClientFactory, options, NameResolver, Serializer, loggerFactory);
        SchedulerResources = new AzureSchedulerResources(ClientFactory, options, NameResolver, Serializer, loggerFactory);
        LeaseResources = new AzureLeaseResources(ClientFactory, options, NameResolver, Serializer, loggerFactory);
        ExternalSideEffectResources = new AzureExternalSideEffectResources(ClientFactory, options, NameResolver, Serializer, loggerFactory);
        FanoutResources = new AzureFanoutResources(ClientFactory, options, NameResolver, Serializer, loggerFactory);

        OutboxJoinStore = new AzureOutboxJoinStore(OutboxResources, timeProvider);
        Outbox = new AzureOutboxService(OutboxResources, timeProvider, OutboxJoinStore, NullLogger<AzureOutboxService>.Instance);
        OutboxStore = new AzureOutboxStore(Outbox, options);
        Inbox = new AzureInboxService(InboxResources, timeProvider, NullLogger<AzureInboxService>.Instance);
        InboxWorkStore = new AzureInboxWorkStore(Inbox);
        SchedulerCore = new AzureSchedulerCore(SchedulerResources, timeProvider, NullLogger<AzureSchedulerCore>.Instance);
        SchedulerClient = new AzureSchedulerClient(SchedulerCore);
        SchedulerStore = new AzureSchedulerStore(SchedulerCore);
        LeaseFactory = new AzureSystemLeaseFactory(LeaseResources, timeProvider, options, NullLogger<AzureSystemLeaseFactory>.Instance);
        ExternalSideEffectStore = new AzureExternalSideEffectStore(ExternalSideEffectResources, timeProvider, NullLogger<AzureExternalSideEffectStore>.Instance);
        FanoutPolicyRepository = new AzureFanoutPolicyRepository(FanoutResources, timeProvider);
        FanoutCursorRepository = new AzureFanoutCursorRepository(FanoutResources);
    }

    public AzurePlatformOptions Options { get; }

    public TimeProvider TimeProvider { get; }

    public AzurePlatformClientFactory ClientFactory { get; }

    public AzurePlatformNameResolver NameResolver { get; }

    public AzurePlatformJsonSerializer Serializer { get; }

    public AzureOutboxResources OutboxResources { get; }

    public AzureInboxResources InboxResources { get; }

    public AzureSchedulerResources SchedulerResources { get; }

    public AzureLeaseResources LeaseResources { get; }

    public AzureExternalSideEffectResources ExternalSideEffectResources { get; }

    public AzureFanoutResources FanoutResources { get; }

    public AzureOutboxJoinStore OutboxJoinStore { get; }

    public AzureOutboxService Outbox { get; }

    public AzureOutboxStore OutboxStore { get; }

    public AzureInboxService Inbox { get; }

    public AzureInboxWorkStore InboxWorkStore { get; }

    public AzureSchedulerCore SchedulerCore { get; }

    public AzureSchedulerClient SchedulerClient { get; }

    public AzureSchedulerStore SchedulerStore { get; }

    public AzureSystemLeaseFactory LeaseFactory { get; }

    public AzureExternalSideEffectStore ExternalSideEffectStore { get; }

    public AzureFanoutPolicyRepository FanoutPolicyRepository { get; }

    public AzureFanoutCursorRepository FanoutCursorRepository { get; }

    public static async Task<AzurePlatformTestContext> CreateAsync(
        string scenarioName,
        TimeProvider? timeProvider = null,
        Action<AzurePlatformOptions>? configure = null)
    {
        AzuriteTestEnvironment environment = await AzuriteTestEnvironment.GetTableAsync().ConfigureAwait(false);
        AzurePlatformOptions options = AzurePlatformTestOptions.CreateIntegrationOptions(environment.ConnectionString, scenarioName, configure);
        AzurePlatformTestContext context = new(options, timeProvider ?? TimeProvider.System);
        await context.ResetAsync().ConfigureAwait(false);
        return context;
    }

    public static async Task ResetAsync(AzurePlatformOptions options)
    {
        AzurePlatformTestContext context = new(options, TimeProvider.System);
        await context.ResetAsync().ConfigureAwait(false);
    }

    public BlobContainerClient GetPayloadContainerClient() =>
        ClientFactory.BlobServiceClient.GetBlobContainerClient(NameResolver.GetPayloadContainerName());

    public QueueClient GetOutboxSignalQueueClient() =>
        ClientFactory.QueueServiceClient.GetQueueClient(NameResolver.GetOutboxSignalQueueName());

    public QueueClient GetInboxSignalQueueClient() =>
        ClientFactory.QueueServiceClient.GetQueueClient(NameResolver.GetInboxSignalQueueName());

    public QueueClient GetSchedulerSignalQueueClient() =>
        ClientFactory.QueueServiceClient.GetQueueClient(NameResolver.GetSchedulerSignalQueueName());

    public async Task ResetAsync()
    {
        await DeleteTableIfExistsAsync(NameResolver.GetOutboxTableName()).ConfigureAwait(false);
        await DeleteTableIfExistsAsync(NameResolver.GetInboxTableName()).ConfigureAwait(false);
        await DeleteTableIfExistsAsync(NameResolver.GetSchedulerTableName()).ConfigureAwait(false);
        await DeleteTableIfExistsAsync(NameResolver.GetLeaseTableName()).ConfigureAwait(false);
        await DeleteTableIfExistsAsync(NameResolver.GetExternalSideEffectTableName()).ConfigureAwait(false);
        await DeleteTableIfExistsAsync(NameResolver.GetFanoutTableName()).ConfigureAwait(false);

        await ClientFactory.QueueServiceClient.GetQueueClient(NameResolver.GetOutboxSignalQueueName())
            .DeleteIfExistsAsync(cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        await ClientFactory.QueueServiceClient.GetQueueClient(NameResolver.GetInboxSignalQueueName())
            .DeleteIfExistsAsync(cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        await ClientFactory.QueueServiceClient.GetQueueClient(NameResolver.GetSchedulerSignalQueueName())
            .DeleteIfExistsAsync(cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        await ClientFactory.BlobServiceClient.GetBlobContainerClient(NameResolver.GetPayloadContainerName())
            .DeleteIfExistsAsync(cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task DeleteTableIfExistsAsync(string tableName)
    {
        try
        {
            await ClientFactory.TableServiceClient
                .GetTableClient(tableName)
                .DeleteAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
        }
    }
}
