# Incursa.Platform.AzureStorage

`Incursa.Platform.AzureStorage` provides an Azure Storage implementation of the Incursa Platform durable workflow mechanics.

It uses:

- Azure Tables for authoritative state
- Azure Blobs for payload offload
- Azure Queues for best-effort wake-up signaling

## Registration

Connection string:

```csharp
services.AddAzurePlatform(options =>
{
    options.ConnectionString = configuration.GetConnectionString("AzureStorage");
});
```

Managed identity / `DefaultAzureCredential`:

```csharp
services.AddAzurePlatform(options =>
{
    options.BlobServiceUri = new Uri("https://my-account.blob.core.windows.net");
    options.QueueServiceUri = new Uri("https://my-account.queue.core.windows.net");
    options.TableServiceUri = new Uri("https://my-account.table.core.windows.net");
});
```

## Supported mechanics

- outbox and outbox store
- inbox and inbox work store
- global outbox / inbox / scheduler / lease wrappers
- scheduler
- fanout policy and cursor repositories
- leases with fencing tokens
- external side-effect store
- outbox joins

No provider-neutral semaphore abstraction currently exists in `Incursa.Platform`, so this package does not add one.

## Important behavior

- Azure Tables are the source of truth for workflow state.
- Azure Queues are advisory wake-up signals only. Duplicate or stale queue messages are safe.
- Payloads larger than `InlinePayloadThresholdBytes` are offloaded to blobs with deterministic paths.
- `IOutbox.EnqueueAsync(..., IDbTransaction, ...)` overloads throw `NotSupportedException`. Azure Storage cannot join an arbitrary ADO.NET transaction.

## Local integration tests

The Azurite-backed test project expects:

- `INCURSA_AZURE_STORAGE_CONNECTION_STRING`
- `INCURSA_AZURE_STORAGE_ENABLE_TABLES=true`

Example connection string:

```text
DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;
```

See [the Azure provider ADR](../../docs/azure-storage-platform-provider-adr.md) for the storage model, guarantee tradeoffs, repair strategy, and unsupported behaviors.
