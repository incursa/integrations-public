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

using Azure.Identity;

namespace Incursa.Platform;

/// <summary>
/// Configures the Azure Storage-backed platform provider.
/// </summary>
public sealed class AzurePlatformOptions
{
    /// <summary>
    /// Gets or sets the Azure Storage connection string.
    /// When provided, service URIs are ignored.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the blob service endpoint used with <see cref="DefaultAzureCredential"/>.
    /// </summary>
    public Uri? BlobServiceUri { get; set; }

    /// <summary>
    /// Gets or sets the queue service endpoint used with <see cref="DefaultAzureCredential"/>.
    /// </summary>
    public Uri? QueueServiceUri { get; set; }

    /// <summary>
    /// Gets or sets the table service endpoint used with <see cref="DefaultAzureCredential"/>.
    /// </summary>
    public Uri? TableServiceUri { get; set; }

    /// <summary>
    /// Gets or sets the environment-aware resource prefix.
    /// </summary>
    public string ResourcePrefix { get; set; } = "Incursa";

    /// <summary>
    /// Gets or sets the environment scope used when deriving names.
    /// </summary>
    public string EnvironmentName { get; set; } = "Default";

    /// <summary>
    /// Gets or sets a value indicating whether tables, queues, and blob containers should be created on first use.
    /// </summary>
    public bool CreateResourcesIfMissing { get; set; } = true;

    /// <summary>
    /// Gets or sets the blob container for oversized payloads.
    /// </summary>
    public string? PayloadContainerName { get; set; }

    /// <summary>
    /// Gets or sets the outbox table name override.
    /// </summary>
    public string? OutboxTableName { get; set; }

    /// <summary>
    /// Gets or sets the inbox table name override.
    /// </summary>
    public string? InboxTableName { get; set; }

    /// <summary>
    /// Gets or sets the scheduler table name override.
    /// </summary>
    public string? SchedulerTableName { get; set; }

    /// <summary>
    /// Gets or sets the lease table name override.
    /// </summary>
    public string? LeaseTableName { get; set; }

    /// <summary>
    /// Gets or sets the external side-effect table name override.
    /// </summary>
    public string? ExternalSideEffectTableName { get; set; }

    /// <summary>
    /// Gets or sets the fanout table name override.
    /// </summary>
    public string? FanoutTableName { get; set; }

    /// <summary>
    /// Gets or sets the outbox signal queue name override.
    /// </summary>
    public string? OutboxSignalQueueName { get; set; }

    /// <summary>
    /// Gets or sets the inbox signal queue name override.
    /// </summary>
    public string? InboxSignalQueueName { get; set; }

    /// <summary>
    /// Gets or sets the scheduler signal queue name override.
    /// </summary>
    public string? SchedulerSignalQueueName { get; set; }

    /// <summary>
    /// Gets or sets the maximum inline payload size before blob offload is used.
    /// </summary>
    public int InlinePayloadThresholdBytes { get; set; } = 32 * 1024;

    /// <summary>
    /// Gets or sets the outbox dispatcher batch size.
    /// </summary>
    public int OutboxBatchSize { get; set; } = 10;

    /// <summary>
    /// Gets or sets the inbox dispatcher batch size.
    /// </summary>
    public int InboxBatchSize { get; set; } = 10;

    /// <summary>
    /// Gets or sets the scheduler dispatcher batch size.
    /// </summary>
    public int SchedulerBatchSize { get; set; } = 10;

    /// <summary>
    /// Gets or sets the worker idle delay used when no work is found.
    /// </summary>
    public TimeSpan WorkerIdleDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the maximum scheduler idle delay.
    /// </summary>
    public TimeSpan SchedulerMaxIdleDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the default claim duration used by workers.
    /// </summary>
    public TimeSpan ClaimLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the default system lease duration.
    /// </summary>
    public TimeSpan CoordinationLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the ratio at which system leases renew before expiry.
    /// </summary>
    public double LeaseRenewPercent { get; set; } = 0.6;

    /// <summary>
    /// Gets or sets the maximum handler attempts before a message is marked failed/dead.
    /// </summary>
    public int MaxHandlerAttempts { get; set; } = 5;

    /// <summary>
    /// Gets or sets a value indicating whether the outbox worker should run.
    /// </summary>
    public bool EnableOutboxWorker { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the inbox worker should run.
    /// </summary>
    public bool EnableInboxWorker { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the scheduler worker should run.
    /// </summary>
    public bool EnableSchedulerWorker { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether queue wake-up signals should be emitted.
    /// </summary>
    public bool EnableQueueSignals { get; set; } = true;

    /// <summary>
    /// Gets or sets the serializer options used for provider persistence.
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = CreateDefaultSerializerOptions();

    internal AzurePlatformOptions Clone() => new(this);

    internal void CopyFrom(AzurePlatformOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);

        ConnectionString = source.ConnectionString;
        BlobServiceUri = source.BlobServiceUri;
        QueueServiceUri = source.QueueServiceUri;
        TableServiceUri = source.TableServiceUri;
        ResourcePrefix = source.ResourcePrefix;
        EnvironmentName = source.EnvironmentName;
        CreateResourcesIfMissing = source.CreateResourcesIfMissing;
        PayloadContainerName = source.PayloadContainerName;
        OutboxTableName = source.OutboxTableName;
        InboxTableName = source.InboxTableName;
        SchedulerTableName = source.SchedulerTableName;
        LeaseTableName = source.LeaseTableName;
        ExternalSideEffectTableName = source.ExternalSideEffectTableName;
        FanoutTableName = source.FanoutTableName;
        OutboxSignalQueueName = source.OutboxSignalQueueName;
        InboxSignalQueueName = source.InboxSignalQueueName;
        SchedulerSignalQueueName = source.SchedulerSignalQueueName;
        InlinePayloadThresholdBytes = source.InlinePayloadThresholdBytes;
        OutboxBatchSize = source.OutboxBatchSize;
        InboxBatchSize = source.InboxBatchSize;
        SchedulerBatchSize = source.SchedulerBatchSize;
        WorkerIdleDelay = source.WorkerIdleDelay;
        SchedulerMaxIdleDelay = source.SchedulerMaxIdleDelay;
        ClaimLeaseDuration = source.ClaimLeaseDuration;
        CoordinationLeaseDuration = source.CoordinationLeaseDuration;
        LeaseRenewPercent = source.LeaseRenewPercent;
        MaxHandlerAttempts = source.MaxHandlerAttempts;
        EnableOutboxWorker = source.EnableOutboxWorker;
        EnableInboxWorker = source.EnableInboxWorker;
        EnableSchedulerWorker = source.EnableSchedulerWorker;
        EnableQueueSignals = source.EnableQueueSignals;
        SerializerOptions = new JsonSerializerOptions(source.SerializerOptions);
    }

    public AzurePlatformOptions()
    {
    }

    private AzurePlatformOptions(AzurePlatformOptions source)
    {
        CopyFrom(source);
    }

    private static JsonSerializerOptions CreateDefaultSerializerOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return options;
    }
}
