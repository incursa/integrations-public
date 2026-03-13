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

internal sealed record AzureOutboxRecordModel
{
    public Guid Id { get; init; }

    public Guid MessageId { get; init; }

    public string Topic { get; init; } = string.Empty;

    public string? CorrelationId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? DueTimeUtc { get; set; }

    public byte Status { get; set; }

    public int RetryCount { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? ProcessedBy { get; set; }

    public string? OwnerToken { get; set; }

    public DateTimeOffset? LockedUntilUtc { get; set; }

    public string? DueRowKey { get; set; }

    public string? LockRowKey { get; set; }

    public AzurePayloadReference Payload { get; init; } = new(null, null, null);
}

internal sealed record AzureOutboxIndexModel
{
    public Guid Id { get; init; }
}

internal sealed record AzureOutboxJoinModel
{
    public Guid JoinId { get; init; }

    public long TenantId { get; init; }

    public int ExpectedSteps { get; init; }

    public int CompletedSteps { get; set; }

    public int FailedSteps { get; set; }

    public byte Status { get; set; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset LastUpdatedUtc { get; set; }

    public AzurePayloadReference? Metadata { get; init; }
}

internal sealed record AzureOutboxJoinMemberModel
{
    public Guid JoinId { get; init; }

    public Guid OutboxMessageId { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? FailedAt { get; set; }
}
