# SQLite Platform provider

`Incursa.Platform.Sqlite` is the local SQLite implementation of the Incursa Platform
storage contracts. It is intended for an independent application or agent running on
one host with one local database file. It is not a distributed database provider.

Use `Incursa.Platform.Postgres` or `Incursa.Platform.SqlServer` when workers on more
than one host must coordinate, when a database file is shared, or when the application
needs provider-level discovery or control-plane services.

## Install and register

Install the provider package and the provider-neutral Platform contracts:

```powershell
dotnet add package Incursa.Platform.Sqlite
```

Create the parent directory before registration. The provider requires a fully
qualified local file path; relative paths, URI-style data sources, drive-relative
paths, and UNC/network paths are rejected.

```csharp
using Incursa.Platform;
using Incursa.Platform.Outbox;

var databasePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Contoso", "agent", "platform.sqlite");
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

services.AddSqlitePlatform(databasePath, options =>
{
    options.EnableSchemaDeployment = true;
    options.EnableScheduling = true;
    options.EnableInbox = true;
    options.EnableOutbox = true;
    options.EnableLeases = true;
    options.EnableIdempotency = true;
    options.EnableFanout = true;
    options.EnableOperations = true;
    options.EnableAudit = true;
    options.EnableHealthChecks = true;
    options.EnableWorkers = true;

    options.BusyTimeoutMilliseconds = 5_000;
    options.MaxRetryCount = 5;
    options.RetryBaseDelay = TimeSpan.FromMilliseconds(50);
    options.WorkerInterval = TimeSpan.FromSeconds(30);
    options.Workers.PollingInterval = TimeSpan.FromSeconds(5);
    options.Workers.BatchSize = 50;
    options.Workers.LeaseDuration = TimeSpan.FromSeconds(30);
    options.Workers.RetryDelay = TimeSpan.FromSeconds(5);
    options.Workers.MaxAttempts = 5;

    options.Inbox.MaxAttempts = 5;
    options.Inbox.RetentionPeriod = TimeSpan.FromDays(7);
    options.Leases.DefaultLeaseDuration = TimeSpan.FromSeconds(30);
});
```

`AddSqlitePlatform` registers one configured connection factory, retry policy,
migration runner, integrity checker, backup service, and the enabled Platform
adapters. The feature flags are registration/schema flags; disabling a feature does
not remove tables that were created by an earlier deployment. Keep one registration
for a database file in a process. Do not register a second provider instance for the
same file with different table names or retry settings.

The provider uses the existing provider-neutral interfaces. Application code should
request `ISchedulerClient`, `ISchedulerStore`, `IInbox`, `IInboxWorkStore`, `IOutbox`,
`IOutboxStore`, `ILeaseFactoryProvider`, `IIdempotencyStore`, fanout repositories,
operation tracking, or audit interfaces from dependency injection rather than using
SQLite-specific SQL.

## Configuration

The root `SqlitePlatformOptions` controls the database and coordination behavior:

| Option | Default | Meaning |
| --- | ---: | --- |
| `DataSource` | required | Fully qualified local database file. |
| `BusyTimeoutMilliseconds` | `5000` | SQLite busy timeout applied to opened connections. |
| `MaxRetryCount` | `5` | Additional bounded retries for `SQLITE_BUSY`/`SQLITE_LOCKED`. |
| `RetryBaseDelay` | `50 ms` | Initial bounded retry delay. |
| `MigrationTableName` | `__IncursaSchemaMigrations` | Journal table for caller-supplied migrations. |
| `ExpectedMigrationId` | unset | Optional latest migration id checked by `SqliteIntegrityChecker`. |
| `WorkerInterval` | `30 s` | Interval for enabled low-resource maintenance work. |
| `Workers.PollingInterval` | `5 s` | Delay between inbox, outbox, and scheduler dispatch passes. |
| `Workers.BatchSize` | `50` | Maximum number of work items claimed per pass. |
| `Workers.LeaseDuration` | `30 s` | Claim lease used by dispatch workers. |
| `Workers.RetryDelay` | `5 s` | Delay after a handler failure before redelivery. |
| `Workers.MaxAttempts` | `5` | Outbox attempts before a failed item is marked failed. Inbox attempts use `Inbox.MaxAttempts`. |

The feature flags are `EnableSchemaDeployment`, `EnableScheduling`,
`EnableInbox`, `EnableOutbox`, `EnableLeases`, `EnableIdempotency`, `EnableFanout`,
`EnableOperations`, `EnableAudit`, `EnableHealthChecks`, and `EnableWorkers`.
Feature-specific settings include inbox retention and attempt limits, lease duration
and renewal fraction, idempotency lock duration, operation and audit table names, and
audit validation limits. Table identifiers are validated before registration. `Workers`
controls bounded dispatch behavior; it is separate from `WorkerInterval`, which controls
the lower-frequency integrity maintenance pass.

Retry applies only to transient SQLite busy/locked results. It does not retry SQL
syntax errors, migration drift, corrupt databases, validation failures, or arbitrary
application exceptions. Keep transactions short so that the timeout and retry bound
remain meaningful.

## Capability and parity boundary

The provider implements the applicable single-host Platform contracts. The following
matrix describes the local semantics and the current boundary:

| Area | SQLite behavior | Boundary or caveat |
| --- | --- | --- |
| One-time timers | Durable timer rows with due time, owner claim, acknowledgement, abandonment, and expiry reaping. | Claims are serialized by a short `BEGIN IMMEDIATE` transaction. |
| Recurring jobs | Create/update/delete, manual trigger, durable job runs, claim/ack/abandon/reap. | Cron expressions are evaluated in UTC; no distributed clock or multi-host scheduler is provided. |
| Inbox | Duplicate observation, optional message hash, enqueue, claim, ack, abandon, fail, revive, and expired-work recovery. | The inbox is local to the configured file. |
| Outbox | Enqueue, delayed delivery, claim, dispatch acknowledgement, reschedule/fail, and transactional enqueue through a SQLite transaction. | Delivery remains at-least-once; protect external handlers with idempotency. |
| Fanout | Local policy and cursor repositories with monotonic, idempotent cursor updates. | One configured repository; no database discovery or cross-host fanout. |
| Fan-in/join | Outbox join membership and idempotent completed/failed member accounting. | Join coordination is limited to this local database. |
| Leases | Acquire, renew, release/expiry, and monotonically increasing fencing tokens. | A fencing token protects cooperating local writers; it cannot fence a process that bypasses the database. |
| Idempotency | Begin/complete/fail reservations, expiry reclaim, and cleanup. | It supports exactly-once *coordination* for a key, not exactly-once external side effects. |
| Operations | Durable operation lifecycle, progress, tags, events, snapshots, and stalled-operation marking. | Retention/alerting remains an application responsibility. |
| Audit | Transactional event and anchor writes plus filtered reads. | Audit storage is local; export/central aggregation is not part of this provider. |
| Health | SQLite scheduler/database health checks when enabled. | Wire the check into the host's health endpoint and alerting system. |
| Metrics | Emits local `System.Diagnostics.Metrics` counters through the `Incursa.Platform.Sqlite` meter. | Subscribe with OpenTelemetry or `MeterListener`; there is no central SQLite metrics exporter or rollup. |
| Discovery/control plane | Deliberately not provided. | Do not add shared-file discovery or multi-host coordination around SQLite. |

SQLite's exactly-once supporting primitives prevent duplicate claims or duplicate
completion records under the provider's transaction rules. They cannot make an HTTP
call, email send, payment, or other external effect exactly once. Use an idempotency
key at the receiving boundary and design handlers to tolerate redelivery.

## File placement and concurrency

Place the database and its `-wal`, `-shm`, or rollback-journal sidecars on local
storage owned by the host. A dedicated application-data directory is preferred over a
system or source-code directory. Do not place the file on SMB, NFS, CIFS, a cloud
sync folder, or another path whose locking and durability semantics are not those of
the local filesystem. The path validator rejects UNC/network paths but cannot prove
that every Unix mount point is local; deployment policy must enforce that boundary.

SQLite permits concurrent readers and serializes writers. The provider uses WAL,
`synchronous=FULL`, foreign-key enforcement, short write transactions, busy timeout,
and bounded retries. Start with one maintenance/dispatch worker per process and a
small total number of local workers (typically two to four) per database file. Increase
that only after measuring lock wait and transaction duration. Do not use unbounded
polling, long-lived write transactions, or a large process fleet against one file.

The single-host rule means that several processes in one host may safely access the
same local file within these limits. It does not mean that independently managed
machines may mount and coordinate through the file.

## Built-in dispatch workers

With `EnableWorkers = true`, registration adds bounded hosted services for integrity
maintenance, inbox dispatch, outbox dispatch, and (when scheduling, outbox, and leases
are enabled) scheduler dispatch. Register one handler per topic with the existing
provider-neutral interfaces:

```csharp
services.AddSingleton<IInboxHandler, CustomerInboxHandler>();
services.AddSingleton<IOutboxHandler, EmailOutboxHandler>();
```

The inbox and outbox workers claim at most `Workers.BatchSize` items per pass, invoke
the matching handler, acknowledge success, and reschedule transient failures after
`Workers.RetryDelay`. Permanent failures, missing handlers, or exhausted attempts are
marked failed; inbox exhaustion is controlled by `Inbox.MaxAttempts`, while outbox
exhaustion is controlled by `Workers.MaxAttempts`. Scheduler dispatch claims due
timers and recurring runs under the local
system lease and writes them to the outbox before acknowledging the schedule row.
Process interruption leaves work claimable after lease expiry. These workers provide
durable at-least-once delivery; handlers must be idempotent and external effects are
not exactly once.

## Migrations and schema deployment

`SqliteMigrationRunner.ApplyAsync` accepts ordered `SqliteMigration` values. It owns
the transaction boundary, obtains a serialized write lock, applies pending SQL, and
records a SHA-256 hash for every migration in the configured journal table. Reapplying
the same migration is a no-op when its SQL hash matches. Changing SQL after an id has
been applied fails with a journal hash mismatch; create a new migration id instead.

Migration SQL must not contain transaction-control statements (`BEGIN`, `COMMIT`,
`ROLLBACK`) or `VACUUM`; the runner owns the transaction. Migration ids must be unique
in the supplied sequence. A failed migration rolls back the migration and journal
record together. Feature adapters deploy their own validated schema when their feature
is first used and schema deployment is enabled. `SqliteSchemaMigrations.GetMigrations`
builds the deterministic bundle for all enabled features, and
`SqliteSchemaMigrations.ApplyAsync` applies that bundle atomically. This is the
recommended startup path when schema deployment is enabled; callers that own schema
deployment can use the migration runner directly.

```csharp
await migrationRunner.ApplyAsync([
    new SqliteMigration(
        "agent-001",
        "CREATE TABLE IF NOT EXISTS AgentSettings (Name TEXT NOT NULL PRIMARY KEY, Value TEXT NOT NULL);"),
    new SqliteMigration(
        "agent-002",
        "CREATE INDEX IF NOT EXISTS IX_AgentSettings_Value ON AgentSettings(Value);")],
    cancellationToken);
```

Run `SqliteIntegrityChecker.CheckAsync` after deployment and before a cutover. It
executes `PRAGMA integrity_check`, `PRAGMA foreign_key_check`, and, when configured,
reports the latest applied migration id against `ExpectedMigrationId`. A missing file
is reported as an error; integrity checks do not create a database.

## Backup, restore, and cutover

Use `SqliteBackupService` for online backup. It checkpoints the WAL, validates source
and destination integrity, uses SQLite's online backup API, writes to a temporary
sibling file, and atomically publishes the result. Existing destinations require
`overwrite: true`. Backup and restore paths must be different from the live database
and its sidecars.

```csharp
await backupService.BackupAsync(
    Path.GetFullPath("backups/platform-2026-10-20.sqlite"),
    overwrite: true,
    cancellationToken);

await backupService.RestoreAsync(
    Path.GetFullPath("backups/platform-2026-10-20.sqlite"),
    Path.GetFullPath("recovery/platform-restored.sqlite"),
    cancellationToken);
```

Restore validates the source before atomically publishing the inactive destination.
It does not replace the configured live database. For a production cutover:

1. Stop all processes that use the live database.
2. Take and verify a final backup.
3. Restore to a new local path and run the integrity and foreign-key checks.
4. Move the old live file and its SQLite sidecars out of the way.
5. Move the verified restored file into the configured path, recreate no sidecars by hand, and start the host.
6. Confirm health, migration id, pending work, and application-level recovery before resuming dispatch.

Keep backup files outside the live database directory when possible. Test restoring a
backup into a clean directory before relying on it for recovery. A backup that passes
SQLite integrity checks is not proof that an external message or side effect was
completed.

## Recovery and operations

On a process interruption, claimed timers, inbox messages, outbox messages, and job
runs become eligible after their lock expiry. The next local worker can reap and
reclaim them. A stale owner cannot acknowledge or release work after ownership has
changed because updates are guarded by owner/fencing tokens. Lease renewal failure
must be treated as loss of authority; stop issuing writes and let the lease expire.

For a suspected corruption or invalid path:

- stop dispatch and preserve the original file and sidecars;
- run `SqliteIntegrityChecker` against a copy or read-only recovery path;
- inspect the last verified backup and migration journal;
- restore to a new path and validate it before cutover;
- investigate filesystem, disk, and process errors before restarting workers.

Do not delete a damaged database or its WAL as a first response. Preserve it for
forensics and retain the failed backup/restore logs.

## .NET Worker Service example

```csharp
using Incursa.Platform;
using Incursa.Platform.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDirectory);

builder.Services.AddSqlitePlatform(
    Path.Combine(dataDirectory, "worker-platform.sqlite"),
    options =>
    {
        options.EnableWorkers = true;
        options.WorkerInterval = TimeSpan.FromSeconds(30);
        options.Workers.PollingInterval = TimeSpan.FromSeconds(5);
        options.Workers.BatchSize = 50;
        options.Workers.LeaseDuration = TimeSpan.FromSeconds(30);
        options.Workers.RetryDelay = TimeSpan.FromSeconds(5);
        options.Workers.MaxAttempts = 5;
        options.EnableHealthChecks = false; // expose health through the worker's own probe
    });
builder.Services.AddSingleton<IInboxHandler, AgentInboxHandler>();
builder.Services.AddSingleton<IOutboxHandler, AgentOutboxHandler>();
builder.Services.AddHostedService<AgentWorker>();

await builder.Build().RunAsync();
```

The worker should register one `IInboxHandler`/`IOutboxHandler` per topic, claim a
bounded batch, perform short handler work, then acknowledge
or abandon each item. On cancellation, abandon only work still owned by the current
owner token; otherwise let the lease expire and allow recovery.

## ASP.NET Core example

```csharp
using Incursa.Platform;
using Incursa.Platform.Outbox;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDirectory);

builder.Services.AddSqlitePlatform(
    Path.Combine(dataDirectory, "web-platform.sqlite"),
    options =>
    {
        options.EnableSchemaDeployment = true;
        options.EnableHealthChecks = true;
        options.EnableWorkers = true;
    });
builder.Services.AddSingleton<IInboxHandler, CustomerInboxHandler>();
builder.Services.AddSingleton<IOutboxHandler, EmailOutboxHandler>();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok("SQLite Platform host is running."));
await app.RunAsync();
```

Keep one host responsible for the file. If the web application is scaled to another
machine, move coordination to PostgreSQL or SQL Server before enabling cross-host
workers.

## Verification

The provider test project is `tests/Incursa.Platform.Sqlite.Tests`. Focused tests cover
WAL and full synchronization, path rejection, transactional migrations and drift
detection, bounded busy retries, integrity checks, online backup/restore, scheduler
claims and recurring jobs, inbox/outbox behavior and joins, local concurrent writers,
leases and stale-owner fencing, idempotency, fanout cursors, operations, audit
transactionality, local metrics, registration, and dispatch workers. The shared
provider conformance lane currently reports 33 passing cases. This is conformance
evidence, not a claim about the final total test count. Run the focused project with:

```powershell
dotnet test tests/Incursa.Platform.Sqlite.Tests/Incursa.Platform.Sqlite.Tests.csproj -c Release
```

Run the full CI solution separately. Existing repository-wide vulnerability-as-error
failures in unrelated packages must be reported separately from SQLite provider test
results.

## Limitations

SQLite is the right boundary for a durable local agent, not a substitute for a
server-grade coordination database. This provider intentionally excludes shared-file
operation, multi-host discovery, control-plane coordination, central metrics rollup,
and guarantees about exactly-once external effects. It also evaluates recurring cron
expressions in UTC. When those semantics are required, use the PostgreSQL or SQL
Server provider.
