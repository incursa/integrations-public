// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

#pragma warning disable MA0048 // The local global-scheduler adapters share one SQLite implementation file.

namespace Incursa.Platform;

/// <summary>Global scheduler client wrapper for the single SQLite store.</summary>
public sealed class SqliteGlobalSchedulerClient : IGlobalSchedulerClient
{
    private readonly ISchedulerClient inner;

    /// <summary>Initializes a global client wrapping a SQLite scheduler client.</summary>
    public SqliteGlobalSchedulerClient(ISchedulerClient inner) => this.inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public Task<string> ScheduleTimerAsync(string topic, string payload, DateTimeOffset dueTime, CancellationToken cancellationToken) => inner.ScheduleTimerAsync(topic, payload, dueTime, cancellationToken);
    public Task<bool> CancelTimerAsync(string timerId, CancellationToken cancellationToken) => inner.CancelTimerAsync(timerId, cancellationToken);
    public Task CreateOrUpdateJobAsync(string jobName, string topic, string cronSchedule, CancellationToken cancellationToken) => inner.CreateOrUpdateJobAsync(jobName, topic, cronSchedule, cancellationToken);
    public Task CreateOrUpdateJobAsync(string jobName, string topic, string cronSchedule, string? payload, CancellationToken cancellationToken) => inner.CreateOrUpdateJobAsync(jobName, topic, cronSchedule, payload, cancellationToken);
    public Task DeleteJobAsync(string jobName, CancellationToken cancellationToken) => inner.DeleteJobAsync(jobName, cancellationToken);
    public Task TriggerJobAsync(string jobName, CancellationToken cancellationToken) => inner.TriggerJobAsync(jobName, cancellationToken);
    public Task<IReadOnlyList<Guid>> ClaimTimersAsync(OwnerToken ownerToken, int leaseSeconds, int batchSize, CancellationToken cancellationToken) => inner.ClaimTimersAsync(ownerToken, leaseSeconds, batchSize, cancellationToken);
    public Task<IReadOnlyList<Guid>> ClaimJobRunsAsync(OwnerToken ownerToken, int leaseSeconds, int batchSize, CancellationToken cancellationToken) => inner.ClaimJobRunsAsync(ownerToken, leaseSeconds, batchSize, cancellationToken);
    public Task AckTimersAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken) => inner.AckTimersAsync(ownerToken, ids, cancellationToken);
    public Task AckJobRunsAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken) => inner.AckJobRunsAsync(ownerToken, ids, cancellationToken);
    public Task AbandonTimersAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken) => inner.AbandonTimersAsync(ownerToken, ids, cancellationToken);
    public Task AbandonJobRunsAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken) => inner.AbandonJobRunsAsync(ownerToken, ids, cancellationToken);
    public Task ReapExpiredTimersAsync(CancellationToken cancellationToken) => inner.ReapExpiredTimersAsync(cancellationToken);
    public Task ReapExpiredJobRunsAsync(CancellationToken cancellationToken) => inner.ReapExpiredJobRunsAsync(cancellationToken);
}

/// <summary>Global scheduler store wrapper for the single SQLite store.</summary>
public sealed class SqliteGlobalSchedulerStore : IGlobalSchedulerStore
{
    private readonly ISchedulerStore inner;

    /// <summary>Initializes a global store wrapping a SQLite scheduler store.</summary>
    public SqliteGlobalSchedulerStore(ISchedulerStore inner) => this.inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public Task<DateTimeOffset?> GetNextEventTimeAsync(CancellationToken cancellationToken = default) => inner.GetNextEventTimeAsync(cancellationToken);
    public Task<int> CreateJobRunsFromDueJobsAsync(ISystemLease lease, CancellationToken cancellationToken = default) => inner.CreateJobRunsFromDueJobsAsync(lease, cancellationToken);
    public Task<IReadOnlyList<(Guid Id, string Topic, string Payload)>> ClaimDueTimersAsync(ISystemLease lease, int batchSize, CancellationToken cancellationToken = default) => inner.ClaimDueTimersAsync(lease, batchSize, cancellationToken);
    public Task<IReadOnlyList<(Guid Id, Guid JobId, string Topic, string Payload)>> ClaimDueJobRunsAsync(ISystemLease lease, int batchSize, CancellationToken cancellationToken = default) => inner.ClaimDueJobRunsAsync(lease, batchSize, cancellationToken);
    public Task UpdateSchedulerStateAsync(ISystemLease lease, CancellationToken cancellationToken = default) => inner.UpdateSchedulerStateAsync(lease, cancellationToken);
}
