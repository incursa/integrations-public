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

using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Platform;

internal static class AzurePlatformOptionsValidationHelper
{
    internal static void ValidateAndThrow<TOptions>(TOptions options, IValidateOptions<TOptions> validator)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(validator);

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, options);
        if (result.Failed)
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(TOptions), result.Failures);
        }
    }
}

internal sealed class AzurePlatformOptionsValidator : IValidateOptions<AzurePlatformOptions>
{
    public ValidateOptionsResult Validate(string? name, AzurePlatformOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("Options are required.");
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionString) &&
            (options.BlobServiceUri is null || options.QueueServiceUri is null || options.TableServiceUri is null))
        {
            return ValidateOptionsResult.Fail("Provide either a connection string or all three service URIs for blobs, queues, and tables.");
        }

        if (string.IsNullOrWhiteSpace(options.ResourcePrefix))
        {
            return ValidateOptionsResult.Fail("ResourcePrefix must be provided.");
        }

        if (string.IsNullOrWhiteSpace(options.EnvironmentName))
        {
            return ValidateOptionsResult.Fail("EnvironmentName must be provided.");
        }

        if (options.InlinePayloadThresholdBytes <= 0 || options.InlinePayloadThresholdBytes > 256 * 1024)
        {
            return ValidateOptionsResult.Fail("InlinePayloadThresholdBytes must be between 1 and 262144 bytes.");
        }

        if (options.OutboxBatchSize <= 0 || options.InboxBatchSize <= 0 || options.SchedulerBatchSize <= 0)
        {
            return ValidateOptionsResult.Fail("Batch sizes must be positive.");
        }

        if (options.MaxHandlerAttempts <= 0)
        {
            return ValidateOptionsResult.Fail("MaxHandlerAttempts must be positive.");
        }

        if (options.ClaimLeaseDuration <= TimeSpan.Zero || options.CoordinationLeaseDuration <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("Lease durations must be positive.");
        }

        if (options.LeaseRenewPercent <= 0 || options.LeaseRenewPercent >= 1)
        {
            return ValidateOptionsResult.Fail("LeaseRenewPercent must be greater than 0 and less than 1.");
        }

        if (!string.IsNullOrWhiteSpace(options.PayloadContainerName) && !IsValidBlobOrQueueName(options.PayloadContainerName))
        {
            return ValidateOptionsResult.Fail("PayloadContainerName must be a valid Azure container name.");
        }

        foreach (string? tableName in new[]
                 {
                     options.OutboxTableName,
                     options.InboxTableName,
                     options.SchedulerTableName,
                     options.LeaseTableName,
                     options.ExternalSideEffectTableName,
                     options.FanoutTableName,
                 })
        {
            if (!string.IsNullOrWhiteSpace(tableName) && !IsValidTableName(tableName))
            {
                return ValidateOptionsResult.Fail($"'{tableName}' is not a valid Azure Table name.");
            }
        }

        foreach (string? queueName in new[]
                 {
                     options.OutboxSignalQueueName,
                     options.InboxSignalQueueName,
                     options.SchedulerSignalQueueName,
                 })
        {
            if (!string.IsNullOrWhiteSpace(queueName) && !IsValidBlobOrQueueName(queueName))
            {
                return ValidateOptionsResult.Fail($"'{queueName}' is not a valid Azure Queue name.");
            }
        }

        return ValidateOptionsResult.Success;
    }

    private static bool IsValidTableName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length is >= 3 and <= 63 &&
        char.IsLetter(value[0]) &&
        value.All(char.IsLetterOrDigit);

    private static bool IsValidBlobOrQueueName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 3 or > 63)
        {
            return false;
        }

        if (value[0] == '-' || value[^1] == '-' || value.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        return value.All(c => char.IsLower(c) || char.IsDigit(c) || c == '-');
    }
}

internal sealed class AzurePlatformClientFactory
{
    public AzurePlatformClientFactory(AzurePlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            BlobServiceClient = new BlobServiceClient(options.ConnectionString);
            QueueServiceClient = new QueueServiceClient(options.ConnectionString);
            TableServiceClient = new TableServiceClient(options.ConnectionString);
            return;
        }

        TokenCredential credential = new DefaultAzureCredential();
        BlobServiceClient = new BlobServiceClient(options.BlobServiceUri!, credential);
        QueueServiceClient = new QueueServiceClient(options.QueueServiceUri!, credential);
        TableServiceClient = new TableServiceClient(options.TableServiceUri!, credential);
    }

    public BlobServiceClient BlobServiceClient { get; }

    public QueueServiceClient QueueServiceClient { get; }

    public TableServiceClient TableServiceClient { get; }
}

internal sealed class AzurePlatformNameResolver
{
    private readonly string tablePrefix;
    private readonly string queuePrefix;
    private readonly AzurePlatformOptions options;

    public AzurePlatformNameResolver(AzurePlatformOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));

        string tableSafePrefix = SanitizeTableComponent(options.ResourcePrefix) + SanitizeTableComponent(options.EnvironmentName);
        tablePrefix = string.IsNullOrWhiteSpace(tableSafePrefix) ? "IncursaAzure" : tableSafePrefix;

        string queueSafePrefix = $"{SanitizeQueueComponent(options.ResourcePrefix)}-{SanitizeQueueComponent(options.EnvironmentName)}";
        queuePrefix = queueSafePrefix.Trim('-');
    }

    public string GetOutboxTableName() => ResolveTableName(options.OutboxTableName, "Outbox");

    public string GetInboxTableName() => ResolveTableName(options.InboxTableName, "Inbox");

    public string GetSchedulerTableName() => ResolveTableName(options.SchedulerTableName, "Scheduler");

    public string GetLeaseTableName() => ResolveTableName(options.LeaseTableName, "Leases");

    public string GetExternalSideEffectTableName() => ResolveTableName(options.ExternalSideEffectTableName, "Effects");

    public string GetFanoutTableName() => ResolveTableName(options.FanoutTableName, "Fanout");

    public string GetPayloadContainerName() => options.PayloadContainerName ?? ResolveQueueOrContainerName("platform-payloads");

    public string GetOutboxSignalQueueName() => options.OutboxSignalQueueName ?? ResolveQueueOrContainerName("outbox-signal");

    public string GetInboxSignalQueueName() => options.InboxSignalQueueName ?? ResolveQueueOrContainerName("inbox-signal");

    public string GetSchedulerSignalQueueName() => options.SchedulerSignalQueueName ?? ResolveQueueOrContainerName("scheduler-signal");

    public string GetPayloadBlobName(string scope, string itemId, string extension = "json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        return $"{SanitizeBlobPathSegment(scope)}/{itemId.Trim()}.{extension}";
    }

    public static string EncodeKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        byte[] bytes = Encoding.UTF8.GetBytes(value.Trim());
        string encoded = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal);

        if (encoded.Length <= 512)
        {
            return encoded;
        }

        byte[] hash = SHA256.HashData(bytes);
        return $"sha256-{Convert.ToHexString(hash)}";
    }

    private string ResolveTableName(string? explicitName, string suffix)
    {
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return explicitName;
        }

        string value = tablePrefix + suffix;
        return value.Length <= 63 ? value : value[..63];
    }

    private string ResolveQueueOrContainerName(string suffix)
    {
        string value = $"{queuePrefix}-{suffix}".Trim('-');
        if (value.Length <= 63)
        {
            return value;
        }

        return value[..63].TrimEnd('-');
    }

    private static string SanitizeTableComponent(string value)
    {
        string sanitized = new(value.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return string.Empty;
        }

        if (!char.IsLetter(sanitized[0]))
        {
            sanitized = "T" + sanitized;
        }

        return sanitized;
    }

    private static string SanitizeQueueComponent(string value)
    {
        string sanitized = new(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());

        while (sanitized.Contains("--", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("--", "-", StringComparison.Ordinal);
        }

        return sanitized.Trim('-');
    }

    private static string SanitizeBlobPathSegment(string value)
    {
        return Uri.EscapeDataString(value.Trim());
    }
}

internal sealed class AzurePlatformJsonSerializer
{
    private readonly JsonSerializerOptions options;

    public AzurePlatformJsonSerializer(AzurePlatformOptions platformOptions)
    {
        ArgumentNullException.ThrowIfNull(platformOptions);
        options = new JsonSerializerOptions(platformOptions.SerializerOptions);
    }

    public byte[] SerializeToBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, options);

    public string SerializeToString<T>(T value) => JsonSerializer.Serialize(value, options);

    public T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, options);

    public T? Deserialize<T>(ReadOnlySpan<byte> json) => JsonSerializer.Deserialize<T>(json, options);

    public async Task<T?> DeserializeAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        return await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken).ConfigureAwait(false);
    }
}

internal static class AzurePlatformRowKeys
{
    public static string Item(string id) => $"item|{id}";

    public static string Due(long dueUnixMilliseconds, string id) => $"due|{dueUnixMilliseconds:D19}|{id}";

    public static string Lock(long lockUnixMilliseconds, string id) => $"lock|{lockUnixMilliseconds:D19}|{id}";

    public static string Join(Guid joinId) => $"join|{joinId:N}";

    public static string JoinMember(Guid joinId, Guid messageId) => $"join-member|{joinId:N}|{messageId:N}";

    public static string Timer(Guid id) => $"timer|{id:N}";

    public static string TimerDue(long dueUnixMilliseconds, Guid id) => $"timer-due|{dueUnixMilliseconds:D19}|{id:N}";

    public static string TimerLock(long lockUnixMilliseconds, Guid id) => $"timer-lock|{lockUnixMilliseconds:D19}|{id:N}";

    public static string Job(string jobName) => $"job|{AzurePlatformNameResolver.EncodeKey(jobName)}";

    public static string JobDue(long dueUnixMilliseconds, Guid jobId) => $"job-due|{dueUnixMilliseconds:D19}|{jobId:N}";

    public static string JobRun(Guid id) => $"job-run|{id:N}";

    public static string JobRunByJob(Guid jobId, Guid runId) => $"job-run-job|{jobId:N}|{runId:N}";

    public static string JobRunDue(long dueUnixMilliseconds, Guid id) => $"job-run-due|{dueUnixMilliseconds:D19}|{id:N}";

    public static string JobRunLock(long lockUnixMilliseconds, Guid id) => $"job-run-lock|{lockUnixMilliseconds:D19}|{id:N}";

    public static string TimerDispatch(long dueUnixMilliseconds, Guid id) => $"timer-dispatch|{dueUnixMilliseconds:D19}|{id:N}";

    public static string JobRunDispatch(long dueUnixMilliseconds, Guid id) => $"job-run-dispatch|{dueUnixMilliseconds:D19}|{id:N}";

    public static string SchedulerState() => "scheduler-state";

    public static string Lease(string resourceName) => $"lease|{AzurePlatformNameResolver.EncodeKey(resourceName)}";

    public static string Effect(string operationName, string idempotencyKey) =>
        $"effect|{AzurePlatformNameResolver.EncodeKey(operationName)}|{AzurePlatformNameResolver.EncodeKey(idempotencyKey)}";

    public static string Cursor(string fanoutTopic, string workKey, string shardKey) =>
        $"cursor|{AzurePlatformNameResolver.EncodeKey(fanoutTopic)}|{AzurePlatformNameResolver.EncodeKey(workKey)}|{AzurePlatformNameResolver.EncodeKey(shardKey)}";

    public static string Policy(string fanoutTopic, string workKey) =>
        $"policy|{AzurePlatformNameResolver.EncodeKey(fanoutTopic)}|{AzurePlatformNameResolver.EncodeKey(workKey)}";
}

internal static class AzurePlatformTableConstants
{
    public const string PartitionKey = "p";
    public const string EntityTypeProperty = "EntityType";
    public const string DataProperty = "Data";
    public const string PayloadInlineProperty = "PayloadInline";
    public const string PayloadBlobProperty = "PayloadBlob";
    public const string PayloadChecksumProperty = "PayloadChecksum";
}

internal static class AzurePlatformExceptionHelper
{
    public static bool IsConflictOrPrecondition(RequestFailedException exception) => exception.Status is 409 or 412;

    public static bool IsNotFound(RequestFailedException exception) => exception.Status == 404;

    public static bool IsInfrastructureFailure(RequestFailedException exception) => exception.Status >= 500;
}

internal sealed class AzurePlatformTable
{
    private readonly AzurePlatformOptions options;
    private readonly ILogger logger;
    private readonly TableClient tableClient;
    private readonly SemaphoreSlim ensureGate = new(1, 1);
    private bool ensured;

    public AzurePlatformTable(
        AzurePlatformClientFactory clientFactory,
        AzurePlatformOptions options,
        ILogger logger,
        string tableName)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        tableClient = clientFactory.TableServiceClient.GetTableClient(tableName);
    }

    public TableClient Client => tableClient;

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (ensured || !options.CreateResourcesIfMissing)
        {
            return;
        }

        await ensureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ensured)
            {
                return;
            }

            logger.LogDebug("Ensuring Azure Table '{TableName}' exists.", tableClient.Name);
            await tableClient.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);
            ensured = true;
        }
        finally
        {
            ensureGate.Release();
        }
    }
}

internal sealed class AzurePlatformBlobContainer
{
    private readonly AzurePlatformOptions options;
    private readonly ILogger logger;
    private readonly BlobContainerClient containerClient;
    private readonly SemaphoreSlim ensureGate = new(1, 1);
    private bool ensured;

    public AzurePlatformBlobContainer(
        AzurePlatformClientFactory clientFactory,
        AzurePlatformOptions options,
        ILogger logger,
        string containerName)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        containerClient = clientFactory.BlobServiceClient.GetBlobContainerClient(containerName);
    }

    public BlobContainerClient Client => containerClient;

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (ensured || !options.CreateResourcesIfMissing)
        {
            return;
        }

        await ensureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ensured)
            {
                return;
            }

            logger.LogDebug("Ensuring Azure Blob container '{ContainerName}' exists.", containerClient.Name);
            await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            ensured = true;
        }
        finally
        {
            ensureGate.Release();
        }
    }
}

internal sealed class AzurePlatformQueue
{
    private readonly AzurePlatformOptions options;
    private readonly AzurePlatformJsonSerializer serializer;
    private readonly ILogger logger;
    private readonly QueueClient queueClient;
    private readonly SemaphoreSlim ensureGate = new(1, 1);
    private bool ensured;

    public AzurePlatformQueue(
        AzurePlatformClientFactory clientFactory,
        AzurePlatformOptions options,
        AzurePlatformJsonSerializer serializer,
        ILogger logger,
        string queueName)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        queueClient = clientFactory.QueueServiceClient.GetQueueClient(queueName);
    }

    public QueueClient Client => queueClient;

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (ensured || !options.CreateResourcesIfMissing)
        {
            return;
        }

        await ensureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ensured)
            {
                return;
            }

            logger.LogDebug("Ensuring Azure Queue '{QueueName}' exists.", queueClient.Name);
            await queueClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            ensured = true;
        }
        finally
        {
            ensureGate.Release();
        }
    }

    public async Task SendSignalAsync(string signalName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);

        if (!options.EnableQueueSignals)
        {
            return;
        }

        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        string encodedBody = Convert.ToBase64String(Encoding.UTF8.GetBytes(serializer.SerializeToString(new AzureSignalEnvelope(signalName))));
        await queueClient.SendMessageAsync(encodedBody, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryReceiveAndDeleteSignalAsync(CancellationToken cancellationToken)
    {
        if (!options.EnableQueueSignals)
        {
            return false;
        }

        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        Response<Azure.Storage.Queues.Models.QueueMessage[]> response = await queueClient.ReceiveMessagesAsync(
                maxMessages: 1,
                visibilityTimeout: TimeSpan.FromSeconds(5),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        Azure.Storage.Queues.Models.QueueMessage? message = response.Value.SingleOrDefault();
        if (message is null)
        {
            return false;
        }

        try
        {
            await queueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzurePlatformExceptionHelper.IsConflictOrPrecondition(exception) ||
                                                       AzurePlatformExceptionHelper.IsNotFound(exception))
        {
            logger.LogDebug(
                exception,
                "Signal queue message {MessageId} was already deleted or its pop receipt was stale.",
                message.MessageId);
        }

        return true;
    }

    private sealed record AzureSignalEnvelope(string Name);
}

internal sealed class AzurePlatformPayloadStore
{
    private readonly AzurePlatformOptions options;
    private readonly AzurePlatformJsonSerializer serializer;
    private readonly AzurePlatformBlobContainer payloadContainer;
    private readonly AzurePlatformNameResolver nameResolver;

    public AzurePlatformPayloadStore(
        AzurePlatformOptions options,
        AzurePlatformJsonSerializer serializer,
        AzurePlatformBlobContainer payloadContainer,
        AzurePlatformNameResolver nameResolver)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        this.payloadContainer = payloadContainer ?? throw new ArgumentNullException(nameof(payloadContainer));
        this.nameResolver = nameResolver ?? throw new ArgumentNullException(nameof(nameResolver));
    }

    public async Task<AzurePayloadReference> StoreTextAsync(
        string scope,
        string itemId,
        string payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        string checksum = Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes.Length <= options.InlinePayloadThresholdBytes)
        {
            return new AzurePayloadReference(payload, null, checksum);
        }

        await payloadContainer.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        string blobName = nameResolver.GetPayloadBlobName(scope, itemId);
        await payloadContainer.Client.GetBlobClient(blobName)
            .UploadAsync(new BinaryData(bytes), overwrite: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new AzurePayloadReference(null, blobName, checksum);
    }

    public async Task<string> ReadTextAsync(AzurePayloadReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (!string.IsNullOrWhiteSpace(reference.PayloadInline))
        {
            return reference.PayloadInline;
        }

        if (string.IsNullOrWhiteSpace(reference.PayloadBlobName))
        {
            return string.Empty;
        }

        await payloadContainer.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var response = await payloadContainer.Client.GetBlobClient(reference.PayloadBlobName)
            .DownloadContentAsync(cancellationToken)
            .ConfigureAwait(false);

        return response.Value.Content.ToString();
    }

    public async Task DeleteIfPresentAsync(string? blobName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(blobName))
        {
            return;
        }

        await payloadContainer.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await payloadContainer.Client.GetBlobClient(blobName)
            .DeleteIfExistsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed record AzurePayloadReference(string? PayloadInline, string? PayloadBlobName, string? PayloadChecksum);

internal static class AzurePlatformDeterministicGuid
{
    public static Guid Create(string scope, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        byte[] input = Encoding.UTF8.GetBytes($"{scope.Trim()}::{key.Trim()}");
        Span<byte> bytes = stackalloc byte[16];
        SHA256.HashData(input).AsSpan(0, 16).CopyTo(bytes);

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes);
    }
}
