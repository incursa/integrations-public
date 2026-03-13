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

internal sealed class AzureSchedulerStore : ISchedulerStore
{
    private readonly AzureSchedulerCore core;

    public AzureSchedulerStore(AzureSchedulerCore core)
    {
        this.core = core ?? throw new ArgumentNullException(nameof(core));
    }

    public Task<DateTimeOffset?> GetNextEventTimeAsync(CancellationToken cancellationToken = default) =>
        core.GetNextEventTimeAsync(cancellationToken);

    public Task<int> CreateJobRunsFromDueJobsAsync(ISystemLease lease, CancellationToken cancellationToken = default) =>
        core.CreateJobRunsFromDueJobsAsync(lease, cancellationToken);

    public Task<IReadOnlyList<(Guid Id, string Topic, string Payload)>> ClaimDueTimersAsync(
        ISystemLease lease,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        core.ClaimDueTimersAsync(lease, batchSize, cancellationToken);

    public Task<IReadOnlyList<(Guid Id, Guid JobId, string Topic, string Payload)>> ClaimDueJobRunsAsync(
        ISystemLease lease,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        core.ClaimDueJobRunsAsync(lease, batchSize, cancellationToken);

    public Task UpdateSchedulerStateAsync(ISystemLease lease, CancellationToken cancellationToken = default) =>
        core.UpdateSchedulerStateAsync(lease, cancellationToken);
}
