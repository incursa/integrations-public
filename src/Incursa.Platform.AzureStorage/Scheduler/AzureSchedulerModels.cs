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

internal sealed record AzureSchedulerTimerModel
{
    public Guid Id { get; init; }

    public string Topic { get; set; } = string.Empty;

    public AzurePayloadReference Payload { get; set; } = new(null, null, null);

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset DueTimeUtc { get; set; }

    public byte Status { get; set; }

    public int RetryCount { get; set; }

    public int DispatchAttemptCount { get; set; }

    public string? LastError { get; set; }

    public string? OwnerToken { get; set; }

    public DateTimeOffset? LockedUntilUtc { get; set; }

    public string? DueRowKey { get; set; }

    public string? LockRowKey { get; set; }

    public string? DispatchRowKey { get; set; }

    public DateTimeOffset? DispatchVisibleAtUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }
}

internal sealed record AzureSchedulerJobModel
{
    public Guid JobId { get; init; }

    public string JobName { get; init; } = string.Empty;

    public string Topic { get; set; } = string.Empty;

    public string CronSchedule { get; set; } = string.Empty;

    public AzurePayloadReference Payload { get; set; } = new(null, null, null);

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public DateTimeOffset NextDueTimeUtc { get; set; }

    public string? DueRowKey { get; set; }
}

internal sealed record AzureSchedulerJobRunModel
{
    public Guid Id { get; init; }

    public Guid JobId { get; init; }

    public string JobName { get; init; } = string.Empty;

    public string Topic { get; set; } = string.Empty;

    public AzurePayloadReference Payload { get; set; } = new(null, null, null);

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset ScheduledTimeUtc { get; set; }

    public byte Status { get; set; }

    public int RetryCount { get; set; }

    public int DispatchAttemptCount { get; set; }

    public string? LastError { get; set; }

    public string? OwnerToken { get; set; }

    public DateTimeOffset? LockedUntilUtc { get; set; }

    public string? DueRowKey { get; set; }

    public string? LockRowKey { get; set; }

    public string? DispatchRowKey { get; set; }

    public string? JobIndexRowKey { get; set; }

    public DateTimeOffset? DispatchVisibleAtUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }
}

internal sealed record AzureSchedulerIndexModel
{
    public Guid Id { get; init; }
}

internal sealed record AzureSchedulerJobDueModel
{
    public Guid JobId { get; init; }

    public string JobName { get; init; } = string.Empty;
}

internal sealed record AzureSchedulerStateModel
{
    public long CurrentFencingToken { get; set; }

    public DateTimeOffset? LastRunAtUtc { get; set; }
}
