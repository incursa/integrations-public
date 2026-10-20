// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.Metrics;

namespace Incursa.Platform;

/// <summary>Low-overhead metrics emitted by the local SQLite provider.</summary>
/// <remarks>
/// The provider emits standard <see cref="Meter"/> instruments so an application
/// can collect them with OpenTelemetry or another <see cref="MeterListener"/>
/// without requiring a provider-specific metrics storage service.
/// </remarks>
internal static class SqlitePlatformMetrics
{
    internal const string MeterName = "Incursa.Platform.Sqlite";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> ConnectionsOpened = Meter.CreateCounter<long>(
        "incursa.platform.sqlite.connections.opened_total",
        unit: "connections",
        description: "SQLite connections opened by the provider.");
    private static readonly Counter<long> ConnectionOpenFailures = Meter.CreateCounter<long>(
        "incursa.platform.sqlite.connections.open_failed_total",
        unit: "failures",
        description: "SQLite connection opens that failed.");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        "incursa.platform.sqlite.retries_total",
        unit: "retries",
        description: "Busy or locked SQLite operations retried.");
    private static readonly Counter<long> RetryExhausted = Meter.CreateCounter<long>(
        "incursa.platform.sqlite.retry_exhausted_total",
        unit: "failures",
        description: "SQLite operations that exhausted their bounded retry budget.");
    private static readonly Counter<long> IntegrityChecks = Meter.CreateCounter<long>(
        "incursa.platform.sqlite.integrity_checks_total",
        unit: "checks",
        description: "SQLite integrity checks completed.");
    private static readonly Counter<long> MigrationsApplied = Meter.CreateCounter<long>(
        "incursa.platform.sqlite.migrations_applied_total",
        unit: "migrations",
        description: "SQLite migrations committed successfully.");
    private static readonly Counter<long> MigrationFailures = Meter.CreateCounter<long>(
        "incursa.platform.sqlite.migration_failures_total",
        unit: "failures",
        description: "SQLite migration transactions that failed.");
    private static readonly Counter<long> WorkerPasses = Meter.CreateCounter<long>(
        "incursa.platform.sqlite.worker_passes_total",
        unit: "passes",
        description: "SQLite maintenance worker passes completed.");
    private static readonly Counter<long> OperationEvents = Meter.CreateCounter<long>(
        "incursa.platform.sqlite.operations.events_total",
        unit: "events",
        description: "SQLite operation events written.");
    private static readonly Counter<long> OperationsStarted = Meter.CreateCounter<long>(
        "incursa.platform.sqlite.operations.started_total",
        unit: "operations",
        description: "SQLite operations started.");
    private static readonly Counter<long> OperationsCompleted = Meter.CreateCounter<long>(
        "incursa.platform.sqlite.operations.completed_total",
        unit: "operations",
        description: "SQLite operations completed.");
    private static readonly Counter<long> OperationProgressUpdates = Meter.CreateCounter<long>(
        "incursa.platform.sqlite.operations.progress_updates_total",
        unit: "updates",
        description: "SQLite operation progress updates written.");
    private static readonly Counter<long> OperationSnapshotsRead = Meter.CreateCounter<long>(
        "incursa.platform.sqlite.operations.snapshots_read_total",
        unit: "snapshots",
        description: "SQLite operation snapshots read.");
    private static readonly Counter<long> AuditEventsWritten = Meter.CreateCounter<long>(
        "incursa.platform.sqlite.audit.events_written_total",
        unit: "events",
        description: "SQLite audit events written.");

    internal static void RecordConnectionOpened(string mode) =>
        ConnectionsOpened.Add(1, new KeyValuePair<string, object?>("mode", mode));

    internal static void RecordConnectionOpenFailure() => ConnectionOpenFailures.Add(1);

    internal static void RecordRetry() => Retries.Add(1);

    internal static void RecordRetryExhausted() => RetryExhausted.Add(1);

    internal static void RecordIntegrityCheck(bool healthy) =>
        IntegrityChecks.Add(1, new KeyValuePair<string, object?>("healthy", healthy ? "true" : "false"));

    internal static void RecordMigrationsApplied(int count)
    {
        if (count > 0)
        {
            MigrationsApplied.Add(count);
        }
    }

    internal static void RecordMigrationFailure() => MigrationFailures.Add(1);

    internal static void RecordWorkerPass(string worker, bool successful) =>
        WorkerPasses.Add(1,
            new KeyValuePair<string, object?>("worker", worker),
            new KeyValuePair<string, object?>("successful", successful ? "true" : "false"));

    internal static void RecordOperationStarted() => OperationsStarted.Add(1);

    internal static void RecordOperationProgressUpdated() => OperationProgressUpdates.Add(1);

    internal static void RecordOperationEvent(string kind) =>
        OperationEvents.Add(1, new KeyValuePair<string, object?>("kind", kind));

    internal static void RecordOperationCompleted(string status) =>
        OperationsCompleted.Add(1, new KeyValuePair<string, object?>("status", status));

    internal static void RecordOperationSnapshotRead(bool found) =>
        OperationSnapshotsRead.Add(1, new KeyValuePair<string, object?>("found", found ? "true" : "false"));

    internal static void RecordAuditEventWritten() => AuditEventsWritten.Add(1);
}
