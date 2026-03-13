<!--
Copyright (c) Incursa

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
-->

# ADR: Azure Storage Provider For Incursa Platform Mechanics

## Status

Accepted for the first Azure Storage provider implementation in `Incursa.Integrations.Public`.

## Context

The existing Incursa platform mechanics have durable SQL Server and PostgreSQL providers. The Azure Storage provider needs to preserve the public semantics of the current platform abstractions without pretending Azure Tables, Queues, and Blobs offer relational guarantees they do not have.

The target capabilities are:

- outbox and outbox store
- inbox and inbox work store
- global outbox / global inbox / global scheduler / global lease equivalents
- scheduler
- fanout cursor repository
- leases with fencing
- external side-effect store
- outbox joins
- semaphores only if a provider-neutral abstraction exists

## Decision Summary

The Azure provider uses:

- Azure Tables as the authoritative source of truth for all durable state
- Azure Blob Storage for payload offload when table-inline payloads become too large
- Azure Queues only for best-effort wake-up signaling, never as the authoritative claim source

Each queue-like subsystem is modeled with a single Azure Table and a single partition so that Azure Table entity-group transactions can atomically move state between:

- authoritative item rows
- due-index rows
- lock-index rows

This trades away horizontal partition scale for substantially better semantic fidelity. The first Azure provider favors correctness and explicit repairability over raw throughput.

## 1. Direct Mappings To Azure Primitives

### Azure Tables

Azure Tables map directly to the durable state machines for:

- outbox messages
- inbox messages
- timers
- recurring jobs
- job runs
- outbox joins and join members
- fanout cursors
- fanout policies
- external side-effect records
- lease records and fencing state
- scheduler fencing state

Within a single subsystem table, the provider uses one partition and multiple row types:

- `item|...` rows are authoritative records
- `due|...` rows are queryable due indexes
- `lock|...` rows are queryable lease-expiry indexes

Because all of those rows are in one table partition, Azure Table transactions can atomically:

- enqueue work by inserting both item and due rows
- claim work by updating the item row, deleting the due row, and inserting a lock row
- acknowledge/fail by updating the item row and deleting the lock row
- reschedule by updating the item row, deleting the lock row, and inserting a new due row
- reap expired claims by updating the item row, deleting the lock row, and inserting a new due row

### Azure Blobs

Blobs map directly to:

- large payload offload for outbox, inbox, timer payloads, job payloads, join metadata, and any other oversized inline text
- deterministic payload storage by subsystem and item identifier

### Azure Queues

Queues map directly only to:

- wake-up hints for outbox workers
- wake-up hints for inbox workers
- wake-up hints for scheduler workers after immediate triggers or newly-due work

Queue messages remain compact and reference subsystem intent only. The queue never owns durable workflow state.

## 2. What Must Be Emulated On Top

The following relational behaviors are emulated in table state rather than delegated to the queue service:

- due-time ordering
- claim leases
- retry counts
- `lastError`
- poison/dead-letter states
- expired-claim reaping
- join membership counters
- recurring job run materialization
- monotonic fencing tokens
- idempotent attach / complete / fail transitions

The provider does not persist queue pop receipts for business state. That would force queue-native claims into abstractions that later acknowledge, fail, or reschedule by ID only. Instead:

- Tables own claim state.
- Queues are advisory only.
- Duplicate or stale queue signals are harmless.

## 3. Guarantees That Are Weaker Than SQL/Postgres

The Azure provider intentionally keeps semantics conservative, but some guarantees are still weaker than the relational providers:

- There is no distributed transaction across Table + Queue + Blob.
- Queue signaling is at-least-once and advisory, not transactional with the authoritative table write.
- Scheduler run creation and queue signaling are eventually consistent across services even when table state transitions are atomic within a table partition.
- Throughput is lower than the relational providers because each subsystem favors a single-partition transactional model for correctness.
- Query flexibility is lower because due scans and indexes are modeled explicitly instead of using arbitrary relational predicates.

## 4. Unsupported Behaviors That Must Fail Fast

### Transaction-Bound Outbox Overloads

`IOutbox.EnqueueAsync(..., IDbTransaction, ...)` is unsupported for Azure Storage and throws `NotSupportedException`.

Reason:

- an arbitrary `IDbTransaction` cannot atomically enlist Azure Tables, Azure Queues, or Azure Blobs
- pretending otherwise would silently break the semantics those overloads were designed to provide

The provider supports only the self-managed outbox enqueue overloads that create Azure-native durable state directly.

### Semaphores

No provider-neutral semaphore abstraction was found in the current platform contracts. The Azure provider does not invent one.

## 5. Reconciliation / Repair / Eventual Consistency Strategy

The provider assumes partial failure is normal and repairs explicitly.

### Table Write Succeeds, Queue Signal Fails

- The authoritative table state is already durable.
- Workers still find the work through normal due-index polling.
- The queue signal only affects latency, not correctness.

### Queue Signal Exists, Record Is Already Done Or Missing

- Claim attempts read the authoritative row before taking ownership.
- If the authoritative row is already terminal or gone, the signal is ignored.

### Blob Offload Exists But Row Transition Fails

- The item row is authoritative.
- Offloaded blobs are named deterministically by subsystem and item ID so orphan blobs are diagnosable and sweepable.

### Reap / Recovery

Each queue-like subsystem exposes explicit reap behavior:

- outbox: expired in-progress claims are returned to ready with a fresh due row
- inbox: expired processing claims are returned to seen with a fresh due row
- scheduler timers/job runs: expired claims are returned to ready with a fresh due row

## 6. Large Payload Plan

Payloads are stored inline only when comfortably below the configured inline threshold.

When a payload exceeds the threshold:

- the authoritative table row stores a blob reference and checksum
- the payload body is written to a blob before the table transaction that records the blob reference

Operational notes:

- queue messages never carry the full business payload
- table rows always store enough metadata to resolve the payload deterministically
- blob paths are deterministic by subsystem and item identifier

## 7. Fencing Token Plan

Blob leases alone are not sufficient because the platform lease abstraction requires a monotonic fencing token.

The provider therefore uses table-native lease records:

- authoritative lease row per resource
- owner token
- `leaseUntilUtc`
- `contextJson`
- monotonically increasing `fencingToken`
- optimistic concurrency via ETag compare-and-swap

Acquire and renew both increment the fencing token. Losing the ability to renew cancels the lease token and causes subsequent guarded operations to fail with `LostLeaseException`.

## 8. Plan For `IDbTransaction` Outbox Overloads

The Azure provider does not emulate relational transaction coupling.

Implementation decision:

- the `IDbTransaction` overloads throw `NotSupportedException`
- the design note, README, and XML docs call that out explicitly

This is intentional because:

- the transaction object is provider-specific ADO state, not an Azure-native unit of work
- wrapping Azure writes around it would create a false impression of atomic commit/rollback behavior

## Naming Convention

The provider derives deterministic resource names from a validated environment-aware prefix.

Defaults are of the form:

- tables: `{Prefix}{Environment}Outbox`, `{Prefix}{Environment}Inbox`, `{Prefix}{Environment}Scheduler`, `{Prefix}{Environment}Leases`, `{Prefix}{Environment}Effects`, `{Prefix}{Environment}Fanout`
- queues: `{prefix}-{environment}-outbox-signal`, `{prefix}-{environment}-inbox-signal`, `{prefix}-{environment}-scheduler-signal`
- blob containers: `{prefix}-{environment}-platform-payloads`

Table names are alphanumeric and start with a letter. Queue and container names are lowercase kebab-case.

## Consequences

### Positive

- authoritative state stays explicit and inspectable
- claim/ack/abandon/fail transitions remain idempotent
- fencing tokens are real
- large payload handling is explicit
- queue duplicates do not threaten correctness

### Negative

- the first provider sacrifices partition scale for transactional simplicity
- queue signaling is still eventually consistent
- relational `IDbTransaction` coupling is unavailable by design

## Follow-Up Validation Focus

The Azure test suite must stress:

- duplicate queue signals
- stale queue-delete / signal handling
- concurrent claims
- expired claim reaping
- lost lease cancellation
- scheduler due-run materialization
- payload offload round-trips
- idempotent outbox-join updates
- external side-effect attempt races
