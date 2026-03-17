# Azure Storage Cross-Repo Reference For LLMs

This document is the fast path for answering "what have we built on Azure Storage?" across the Incursa split repositories.

The Azure Storage work is spread across two repos and two layers:

1. `C:\src\incursa\platform` holds the provider-neutral contracts and workflow abstractions.
2. `C:\src\incursa\integrations-public` holds the Azure-backed implementations.

The most important architectural distinction is that there are two separate Azure stories:

- `Incursa.Integrations.Storage.Azure` is the generic Azure implementation of the low-level `Incursa.Platform.Storage` substrate.
- `Incursa.Platform.AzureStorage` is the Azure implementation of the higher-level durable platform mechanics such as inbox, outbox, scheduler, leases, fanout, and external side effects.

Those two layers both use Azure Tables, Blobs, and Queues, but they do not model work and leases the same way.

## Start Here

Use this lookup when answering questions:

| Question | Primary place to read |
| --- | --- |
| What are the raw storage interfaces? | `C:\src\incursa\platform\src\Incursa.Platform.Storage\StorageInterfaces.cs` |
| What key/value, payload, work, and coordination types exist? | `C:\src\incursa\platform\src\Incursa.Platform.Storage\*.cs` |
| What are the provider-neutral inbox/outbox contracts? | `C:\src\incursa\platform\src\Incursa.Platform\Inbox\*.cs` and `C:\src\incursa\platform\src\Incursa.Platform\Outbox\*.cs` |
| How is the generic Azure storage substrate implemented? | `src/Incursa.Integrations.Storage.Azure/` |
| How are inbox/outbox/scheduler/leases implemented on Azure Storage? | `src/Incursa.Platform.AzureStorage/` |
| What is the Azure design rationale and tradeoff model? | `docs/azure-storage-platform-provider-adr.md` |
| What tests prove the Azure behavior? | `tests/Incursa.Integrations.Storage.Azure.Tests/` and `tests/Incursa.Platform.AzureStorage.Tests/` |

## Repository Split

### Provider-neutral contracts in `platform`

Important packages and files:

- `C:\src\incursa\platform\src\Incursa.Platform.Storage\`
  - `StorageInterfaces.cs`
  - `PayloadContracts.cs`
  - `WorkContracts.cs`
  - `CoordinationContracts.cs`
  - `StorageKeyPrimitives.cs`
  - `StorageWriteCondition.cs`
  - `StorageBatch.cs`
- `C:\src\incursa\platform\src\Incursa.Platform\Inbox\`
- `C:\src\incursa\platform\src\Incursa.Platform\Outbox\`
- `C:\src\incursa\platform\src\Incursa.Platform\Scheduler\`
- `C:\src\incursa\platform\src\Incursa.Platform\Lease\`
- `C:\src\incursa\platform\src\Incursa.Platform\ExternalSideEffects\`
- `C:\src\incursa\platform\src\Incursa.Platform\Fanout\`

### Azure implementations in `integrations-public`

Important packages and files:

- `src/Incursa.Integrations.Storage.Azure/`
  - `AzureStorageOptions.cs`
  - `AzureStorageServiceCollectionExtensions.cs`
  - `AzureStorageInfrastructure.cs`
  - `AzureTableStores.cs`
  - `AzureBlobPayloadStore.cs`
  - `AzureQueueWorkStore.cs`
  - `AzureCoordinationStore.cs`
- `src/Incursa.Platform.AzureStorage/`
  - `AzurePlatformOptions.cs`
  - `AzurePlatformServiceCollectionExtensions.cs`
  - `AzurePlatformInfrastructure.cs`
  - `AzurePlatformProviders.cs`
  - `AzurePlatformWorkers.cs`
  - `Inbox/AzureInboxService.cs`
  - `Outbox/AzureOutboxService.cs`
  - `Outbox/AzureOutboxJoinStore.cs`
  - `Lease/AzureSystemLease.cs`
  - `Scheduler/*.cs`
  - `ExternalSideEffects/AzureExternalSideEffectStore.cs`
  - `Fanout/AzureFanoutRepositories.cs`

## Layer 1: Provider-Neutral Raw Storage Interfaces

The raw storage substrate is defined in `Incursa.Platform.Storage`.

Core interfaces:

- `IRecordStore<TRecord>`: partition-aware exact-key record read/write/delete plus same-partition batch execution.
- `ILookupStore<TLookup>`: simpler projection store with exact-key get, upsert, delete, and partition scans.
- `IPayloadStore`: typed JSON payloads plus raw binary streams and metadata.
- `IWorkStore<TWorkItem>`: enqueue, claim, complete, and abandon semantics.
- `ICoordinationStore`: idempotency markers, leases, and checkpoints.

Core supporting types:

- `StoragePartitionKey`, `StorageRowKey`, `StorageRecordKey`, `StoragePayloadKey`
- `StorageETag`
- `WorkClaimToken`
- `CoordinationLeaseToken`
- `PayloadMetadata`, `PayloadReadResult<T>`, `PayloadStreamResult`
- `WorkItem<T>`, `ClaimedWorkItem<T>`, `WorkEnqueueOptions`, `WorkClaimOptions`, `WorkReleaseOptions`
- `CoordinationLeaseRequest`, `CoordinationLease`
- `StorageWriteCondition`, `StorageWriteMode`, `StorageConsistencyMode`
- `StorageBatch<T>` and `StorageBatchOperation<T>`

Important semantics from this layer:

- optimistic concurrency is expressed through opaque provider ETags
- partition-bounded scans are explicit
- single-partition atomic intent is modeled separately from cross-partition eventual-consistency intent
- work is modeled as claim/complete/abandon, not strict FIFO queue semantics

## Layer 1 Azure Implementation: `Incursa.Integrations.Storage.Azure`

This package is the Azure-backed implementation of the raw storage substrate.

Registration:

- `AddAzureStorage(Action<AzureStorageOptions>)`
- `AddAzureStorage(string connectionString, Action<AzureStorageOptions>?)`
- `AddAzureStorage(AzureStorageOptions)`

Registered services:

- `IPayloadStore -> AzureBlobPayloadStore`
- `ICoordinationStore -> AzureCoordinationStore`
- `IRecordStore<T> -> AzureTableRecordStore<T>`
- `ILookupStore<T> -> AzureTableLookupStore<T>`
- `IWorkStore<T> -> AzureQueueWorkStore<T>`

### Azure substrate resource model

`AzureStorageOptions` supports either:

- a connection string, or
- `BlobServiceUri` + `QueueServiceUri` + `TableServiceUri` with `DefaultAzureCredential`

Resource naming/options:

- record tables use `RecordTablePrefix` plus per-type overrides in `RecordTables`
- lookup tables use `LookupTablePrefix` plus per-type overrides in `LookupTables`
- payloads go to `PayloadContainerName`
- work items use queues derived from `WorkQueuePrefix` plus per-type overrides in `WorkQueues`
- oversized work payloads go to `WorkPayloadContainerName`
- coordination markers and checkpoints use `CoordinationTableName`
- coordination leases use `CoordinationContainerName`
- `CreateResourcesIfMissing` lazily creates tables, queues, and containers on first use
- `WorkMessageInlineThresholdBytes` defaults to `48 * 1024`

### How the raw Azure substrate maps to Azure services

| Contract | Azure implementation | Backing resource |
| --- | --- | --- |
| `IRecordStore<T>` | `AzureTableRecordStore<T>` | Azure Table per record type |
| `ILookupStore<T>` | `AzureTableLookupStore<T>` | Azure Table per lookup type |
| `IPayloadStore` | `AzureBlobPayloadStore` | Azure Blob container |
| `IWorkStore<T>` | `AzureQueueWorkStore<T>` | Azure Queue plus blob overflow container |
| `ICoordinationStore` markers/checkpoints | `AzureCoordinationStore` | Azure Table |
| `ICoordinationStore` leases | `AzureCoordinationStore` | Azure Blob leases |

### Important raw-layer behavior

- `AzureTableRecordStore<T>` and `AzureTableLookupStore<T>` are true table-backed stores and support exact-key access plus partition scans.
- `AzureBlobPayloadStore` stores typed JSON and raw binary payloads in blob storage and exposes provider-neutral metadata.
- `AzureQueueWorkStore<T>` uses Azure Queue as the authoritative work queue for the raw `IWorkStore<T>` contract.
- If a serialized work payload exceeds `WorkMessageInlineThresholdBytes`, `AzureQueueWorkStore<T>` stores the payload in blob storage and enqueues a queue message that contains a blob reference instead of the full payload.
- `AzureCoordinationStore` stores idempotency markers and checkpoints in table storage, but lease acquisition uses blob leases rather than table-native fencing records.

### Important raw-layer limitations and unsupported cases

- record-store batch execution rejects `StorageConsistencyMode.CrossPartitionEventuallyConsistent`; batches are only supported for single-partition atomic intent
- delete operations do not support `IfNotExists` preconditions
- some checkpoint write conditions are explicitly unsupported
- payload delete does not support `IfNotExists`
- coordination lease duration is constrained by Azure blob lease rules; tests explicitly verify durations below 15 seconds are rejected

This means the generic Azure substrate is a good fit for raw storage primitives, but it is not the mechanism used to preserve the stronger workflow semantics of the platform inbox/outbox/scheduler stack.

## Layer 2: Provider-Neutral Workflow Contracts

The higher-level workflow abstractions live in `C:\src\incursa\platform\src\Incursa.Platform\`.

Inbox-related contracts:

- `IInbox`
- `IInboxWorkStore`
- `IInboxHandler`
- `IInboxRouter`
- `IInboxWorkStoreProvider`
- `IGlobalInbox`
- `IGlobalInboxWorkStore`

Outbox-related contracts:

- `IOutbox`
- `IOutboxStore`
- `IOutboxJoinStore`
- `IOutboxHandler`
- `IOutboxRouter`
- `IOutboxStoreProvider`
- `IGlobalOutbox`
- `IGlobalOutboxStore`

Also relevant:

- scheduler: `ISchedulerClient`, `ISchedulerStore`, `ISchedulerRouter`, `IGlobalSchedulerClient`, `IGlobalSchedulerStore`
- leases: `ISystemLease`, `ISystemLeaseFactory`, `ILeaseFactoryProvider`, `ILeaseRouter`, `IGlobalSystemLeaseFactory`
- side effects: `IExternalSideEffectStore`, `IExternalSideEffectCoordinator`
- fanout: `IFanoutPolicyRepository`, `IFanoutCursorRepository`, `IFanoutRouter`

Important specs:

- `C:\src\incursa\platform\specs\inbox-specification.md`
- `C:\src\incursa\platform\specs\outbox-specification.md`

Those specs were originally written around SQL-backed implementations, but they define the intended semantics that the Azure provider is trying to preserve.

## Layer 2 Azure Workflow Provider: `Incursa.Platform.AzureStorage`

This package does not simply wrap the raw `IWorkStore<T>` or `ICoordinationStore` implementation.

Instead, it implements the platform workflow mechanics directly on Azure Tables, Blobs, and Queues in order to preserve the inbox/outbox/scheduler semantics as closely as possible.

Registration:

- `AddAzurePlatform(Action<AzurePlatformOptions>)`
- `AddAzurePlatform(AzurePlatformOptions)`

Key options:

- connection string or `BlobServiceUri`/`QueueServiceUri`/`TableServiceUri`
- `ResourcePrefix`
- `EnvironmentName`
- optional overrides for table, queue, and blob names
- `InlinePayloadThresholdBytes` default `32 * 1024`
- `OutboxBatchSize`, `InboxBatchSize`, `SchedulerBatchSize`
- `ClaimLeaseDuration`
- `CoordinationLeaseDuration`
- `LeaseRenewPercent`
- `MaxHandlerAttempts`
- `EnableOutboxWorker`, `EnableInboxWorker`, `EnableSchedulerWorker`
- `EnableQueueSignals`
- `CreateResourcesIfMissing`

### Azure workflow-provider design

This provider follows the accepted ADR in `docs/azure-storage-platform-provider-adr.md`.

Core design rules:

- Azure Tables are the authoritative durable state.
- Azure Blobs are used for payload offload.
- Azure Queues are advisory wake-up signals only.
- Each queue-like subsystem uses a single table and a single partition for semantic fidelity.
- Row-key prefixes are used to model both authoritative records and queryable indexes.

Important row-key patterns from `AzurePlatformInfrastructure.cs`:

- `item|...` rows: authoritative record rows
- `due|...` rows: due-time index rows
- `lock|...` rows: active-lease index rows
- scheduler-specific variants such as `timer-due|...`, `job-due|...`, `job-run-due|...`
- lease rows use `lease|...`

This single-partition design is a deliberate tradeoff: lower scale, but Azure Table transactions can atomically update an item row and its due/lock index rows.

### Resource defaults and naming

If not overridden, names are derived from `ResourcePrefix` and `EnvironmentName`.

Examples:

- tables: outbox, inbox, scheduler, leases, external side effects, fanout
- queues: outbox signal, inbox signal, scheduler signal
- blobs: shared payload container

The provider also exposes a canonical provider key through `AzurePlatformStoreKeyRegistry`:

- `azure-storage:{ResourcePrefix}:{EnvironmentName}`

That key is used to satisfy provider-neutral router/provider abstractions even though the Azure provider is effectively a single configured backend, not a multi-database fanout like the SQL/Postgres providers.

## Azure Inbox Implementation

Main files:

- `src/Incursa.Platform.AzureStorage/Inbox/AzureInboxService.cs`
- `src/Incursa.Platform.AzureStorage/Inbox/AzureInboxModels.cs`
- `src/Incursa.Platform.AzureStorage/Inbox/AzureInboxResources.cs`

Primary types:

- `AzureInboxService : IInbox`
- `AzureInboxWorkStore : IInboxWorkStore`
- `AzureGlobalInbox : IGlobalInbox`
- `AzureGlobalInboxWorkStore : IGlobalInboxWorkStore`

Important behavior:

- `AlreadyProcessedAsync` checks the authoritative inbox item row and inserts a `Seen` row if one does not exist.
- `EnqueueAsync` writes the inbox item plus a due index row in one table transaction.
- Payload text is stored through the shared Azure payload offload helper.
- duplicate enqueue attempts update the existing record rather than blindly inserting another work item
- a claimed message removes the due row and adds a lock row atomically
- `AckAsync`, `AbandonAsync`, `FailAsync`, `ReviveAsync`, and `ReapExpiredAsync` all operate by rewriting the authoritative item row and its related due/lock index rows
- reaping restores expired claims to a due state and emits a wake-up signal
- queue signals are only latency hints; correctness comes from the table rows

Status model:

- seen
- processing
- done
- dead

The inbox worker is hosted as `AzureInboxWorker` in `AzurePlatformWorkers.cs` and dispatches claimed messages to registered `IInboxHandler` implementations.

## Azure Outbox Implementation

Main files:

- `src/Incursa.Platform.AzureStorage/Outbox/AzureOutboxService.cs`
- `src/Incursa.Platform.AzureStorage/Outbox/AzureOutboxModels.cs`
- `src/Incursa.Platform.AzureStorage/Outbox/AzureOutboxResources.cs`
- `src/Incursa.Platform.AzureStorage/Outbox/AzureGlobalOutbox.cs`
- `src/Incursa.Platform.AzureStorage/Outbox/AzureOutboxJoinStore.cs`

Primary types:

- `AzureOutboxService : IOutbox`
- `AzureOutboxStore : IOutboxStore`
- `AzureGlobalOutbox : IGlobalOutbox`
- `AzureGlobalOutboxStore : IGlobalOutboxStore`
- `AzureOutboxJoinStore : IOutboxJoinStore`

Important behavior:

- enqueue writes an authoritative outbox item row and a due row in one table transaction
- payload text is persisted through the shared Azure payload helper and can be offloaded to blobs when it exceeds `InlinePayloadThresholdBytes`
- claiming due messages atomically updates the item row, deletes the due row, and inserts a lock row
- ack, reschedule, fail, and reap all operate on the table row plus due/lock rows
- if a message becomes due immediately, the provider sends an advisory queue signal to reduce latency
- duplicate or stale queue signals are safe and ignored when the table state says there is nothing to do

### Azure outbox transaction limitation

This is one of the most important Azure-specific differences from SQL/Postgres:

- `IOutbox.EnqueueAsync(..., IDbTransaction, ...)` throws `NotSupportedException`
- the Azure provider only supports Azure-native enqueue operations that manage their own durability
- it does not pretend that an arbitrary ADO.NET transaction can atomically cover Azure Table, Blob, and Queue writes

This limitation is called out in the README, ADR, and implementation.

### Azure outbox joins

`AzureOutboxJoinStore` implements `IOutboxJoinStore`.

It supports:

- creating joins
- attaching messages to joins
- reading join state
- incrementing completed and failed counts idempotently
- updating join status
- listing member messages

Join records and join-member records are updated with Azure Table optimistic concurrency and table transactions.

## Scheduler, Leases, Fanout, and External Side Effects

Although inbox and outbox are the main focus, the Azure provider also implements the rest of the durable workflow stack:

- scheduler: `src/Incursa.Platform.AzureStorage/Scheduler/`
- leases with fencing tokens: `src/Incursa.Platform.AzureStorage/Lease/AzureSystemLease.cs`
- external side effects: `src/Incursa.Platform.AzureStorage/ExternalSideEffects/AzureExternalSideEffectStore.cs`
- fanout policy and cursor repositories: `src/Incursa.Platform.AzureStorage/Fanout/AzureFanoutRepositories.cs`

### Azure scheduler

Important points:

- scheduler state is table-backed, not queue-backed
- due timers, due recurring jobs, and job runs use explicit due-row prefixes
- the scheduler worker uses an `ISystemLease` for fencing and safety
- queue signals are used only to wake the worker faster when new due work appears

### Azure leases

The platform lease implementation is different from the raw `ICoordinationStore` blob-lease implementation.

`AzureSystemLeaseFactory` and `AzureSystemLease` use a lease table row with:

- resource name
- owner token
- lease-until timestamp
- monotonically increasing fencing token
- optimistic concurrency through Azure Table ETags

Acquire and renew both advance the fencing token. Losing renewability causes the lease token to become unusable and `ThrowIfLost()` raises `LostLeaseException`.

This is how the Azure workflow provider preserves the platform's fencing-token semantics.

### Azure external side effects and fanout

The provider includes first-class Azure storage implementations for:

- `IExternalSideEffectStore`
- `IFanoutPolicyRepository`
- `IFanoutCursorRepository`

These live in the same package because they are part of the same durable workflow family and reuse the same Azure naming, serialization, and table-transaction patterns.

## The Most Important Architectural Nuance

Do not collapse these two statements together:

- "Azure uses queues directly for raw `IWorkStore<T>`."
- "Azure uses queues only as advisory wake-up signals for the platform inbox/outbox/scheduler provider."

Both are true, but they apply to different layers.

### Raw substrate truth

In `Incursa.Integrations.Storage.Azure`, `IWorkStore<T>` is a real Azure Queue-backed work store with optional blob overflow.

### Workflow-provider truth

In `Incursa.Platform.AzureStorage`, the authoritative durable state for inbox/outbox/scheduler lives in Azure Tables, and Azure Queues are only hints to wake workers.

That design exists because the platform workflow contracts need claim/reap/retry/fencing/join semantics that are not a natural fit for treating Azure Queue as the source of truth.

## Important Known Tradeoffs and Limits

From the ADR and implementation:

- no distributed transaction across Table + Queue + Blob
- queue signaling is at-least-once and advisory
- throughput is intentionally constrained by the single-partition-per-subsystem design
- query flexibility is lower than relational providers because indexes are modeled explicitly as rows
- Azure outbox does not support `IDbTransaction` overloads
- no provider-neutral semaphore abstraction exists, so the Azure provider does not invent one

## Test Coverage Map

Raw Azure storage substrate tests:

- `tests/Incursa.Integrations.Storage.Azure.Tests/AzureStorageOptionsValidatorTests.cs`
- `tests/Incursa.Integrations.Storage.Azure.Tests/AzureStorageNameResolverTests.cs`
- `tests/Incursa.Integrations.Storage.Azure.Tests/AzureStorageSerializerTests.cs`
- `tests/Incursa.Integrations.Storage.Azure.Tests/AzureStorageRegistrationTests.cs`
- `tests/Incursa.Integrations.Storage.Azure.Tests/AzureStorageContractBehaviorTests.cs`
- `tests/Incursa.Integrations.Storage.Azure.Tests/AzureBlobPayloadStoreIntegrationTests.cs`
- `tests/Incursa.Integrations.Storage.Azure.Tests/AzureQueueWorkStoreIntegrationTests.cs`
- `tests/Incursa.Integrations.Storage.Azure.Tests/AzureTableStoreIntegrationTests.cs`

Azure workflow-provider tests:

- `tests/Incursa.Platform.AzureStorage.Tests/AzurePlatformOptionsValidatorTests.cs`
- `tests/Incursa.Platform.AzureStorage.Tests/AzurePlatformRegistrationTests.cs`
- `tests/Incursa.Platform.AzureStorage.Tests/AzureInboxWorkStoreBehaviorTests.cs`
- `tests/Incursa.Platform.AzureStorage.Tests/AzureOutboxStoreBehaviorTests.cs`
- `tests/Incursa.Platform.AzureStorage.Tests/AzureOutboxJoinStoreIntegrationTests.cs`
- `tests/Incursa.Platform.AzureStorage.Tests/AzurePayloadOffloadIntegrationTests.cs`
- `tests/Incursa.Platform.AzureStorage.Tests/AzureQueueSignalIntegrationTests.cs`
- `tests/Incursa.Platform.AzureStorage.Tests/AzureSchedulerBehaviorTests.cs`
- `tests/Incursa.Platform.AzureStorage.Tests/AzureSchedulerAdditionalIntegrationTests.cs`
- `tests/Incursa.Platform.AzureStorage.Tests/AzureConcurrencyStressIntegrationTests.cs`
- `tests/Incursa.Platform.AzureStorage.Tests/AzureSystemLeaseBehaviorTests.cs`
- `tests/Incursa.Platform.AzureStorage.Tests/AzureWorkerIntegrationTests.cs`
- `tests/Incursa.Platform.AzureStorage.Tests/AzureExternalSideEffectStoreIntegrationTests.cs`

Test environment notes:

- both test projects use `INCURSA_AZURE_STORAGE_CONNECTION_STRING` and can target Azurite or a real Azure Storage account
- the Azure platform stress tests auto-start an Azurite Docker container when `INCURSA_AZURE_STORAGE_CONNECTION_STRING` is unset
- `INCURSA_AZURE_STORAGE_ENABLE_TABLES=true` is only required when using Azurite table support
- low-level storage tests explicitly avoid claiming unsupported Azure semantics
- workflow-provider tests cover duplicate signals, payload offload, worker retries, scheduler dispatch, and fencing/lease behavior
- the Azure platform test project also exposes a `Category=Stress` lane for repeated concurrent ingress, outbox, claim, and lease-acquisition races

## Supporting Documentation

Important docs in `integrations-public`:

- `docs/azure-storage-platform-provider-adr.md`
- `src/Incursa.Integrations.Storage.Azure/README.md`
- `src/Incursa.Platform.AzureStorage/README.md`

Important docs/specs in `platform`:

- `C:\src\incursa\platform\src\Incursa.Platform.Storage\README.md`
- `C:\src\incursa\platform\specs\inbox-specification.md`
- `C:\src\incursa\platform\specs\outbox-specification.md`

## Short Answer Summary

If you need the shortest accurate summary:

- raw Azure storage interfaces are defined in `Incursa.Platform.Storage` in the `platform` repo
- their generic Azure implementation lives in `Incursa.Integrations.Storage.Azure`
- the Azure inbox/outbox/scheduler/lease/fanout/side-effect provider lives in `Incursa.Platform.AzureStorage`
- raw `IWorkStore<T>` is queue-backed, but platform inbox/outbox/scheduler state is table-backed with queues used only as advisory wake-up signals
- the Azure workflow provider intentionally trades scale for semantic fidelity by using single-partition table transactions plus blob offload and advisory queue signals
