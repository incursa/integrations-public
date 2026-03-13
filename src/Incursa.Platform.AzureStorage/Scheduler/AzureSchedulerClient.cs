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

namespace Incursa.Platform;

internal sealed class AzureSchedulerClient : ISchedulerClient
{
    private readonly AzureSchedulerCore core;

    public AzureSchedulerClient(AzureSchedulerCore core)
    {
        this.core = core ?? throw new ArgumentNullException(nameof(core));
    }

    internal AzureSchedulerCore Core => core;

    public Task<string> ScheduleTimerAsync(string topic, string payload, DateTimeOffset dueTime, CancellationToken cancellationToken) =>
        core.ScheduleTimerAsync(topic, payload, dueTime, cancellationToken);

    public Task<bool> CancelTimerAsync(string timerId, CancellationToken cancellationToken) =>
        core.CancelTimerAsync(timerId, cancellationToken);

    public Task CreateOrUpdateJobAsync(string jobName, string topic, string cronSchedule, CancellationToken cancellationToken) =>
        core.CreateOrUpdateJobAsync(jobName, topic, cronSchedule, cancellationToken);

    public Task CreateOrUpdateJobAsync(string jobName, string topic, string cronSchedule, string? payload, CancellationToken cancellationToken) =>
        core.CreateOrUpdateJobAsync(jobName, topic, cronSchedule, payload, cancellationToken);

    public Task DeleteJobAsync(string jobName, CancellationToken cancellationToken) =>
        core.DeleteJobAsync(jobName, cancellationToken);

    public Task TriggerJobAsync(string jobName, CancellationToken cancellationToken) =>
        core.TriggerJobAsync(jobName, cancellationToken);

    public Task<IReadOnlyList<Guid>> ClaimTimersAsync(OwnerToken ownerToken, int leaseSeconds, int batchSize, CancellationToken cancellationToken) =>
        core.ClaimTimersAsync(ownerToken, leaseSeconds, batchSize, cancellationToken);

    public Task<IReadOnlyList<Guid>> ClaimJobRunsAsync(OwnerToken ownerToken, int leaseSeconds, int batchSize, CancellationToken cancellationToken) =>
        core.ClaimJobRunsAsync(ownerToken, leaseSeconds, batchSize, cancellationToken);

    public Task AckTimersAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken) =>
        core.AckTimersAsync(ownerToken, ids, cancellationToken);

    public Task AckJobRunsAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken) =>
        core.AckJobRunsAsync(ownerToken, ids, cancellationToken);

    public Task AbandonTimersAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken) =>
        core.AbandonTimersAsync(ownerToken, ids, cancellationToken);

    public Task AbandonJobRunsAsync(OwnerToken ownerToken, IEnumerable<Guid> ids, CancellationToken cancellationToken) =>
        core.AbandonJobRunsAsync(ownerToken, ids, cancellationToken);

    public Task ReapExpiredTimersAsync(CancellationToken cancellationToken) =>
        core.ReapExpiredTimersAsync(cancellationToken);

    public Task ReapExpiredJobRunsAsync(CancellationToken cancellationToken) =>
        core.ReapExpiredJobRunsAsync(cancellationToken);
}
