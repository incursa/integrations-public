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

internal sealed record AzureInboxRecordModel
{
    public string MessageId { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Topic { get; set; } = string.Empty;

    public AzurePayloadReference Payload { get; set; } = new(null, null, null);

    public string? HashBase64 { get; set; }

    public int Attempt { get; set; }

    public DateTimeOffset FirstSeenUtc { get; init; }

    public DateTimeOffset LastSeenUtc { get; set; }

    public DateTimeOffset? ProcessedUtc { get; set; }

    public DateTimeOffset? DueTimeUtc { get; set; }

    public string? LastError { get; set; }

    public byte Status { get; set; }

    public string? OwnerToken { get; set; }

    public DateTimeOffset? LockedUntilUtc { get; set; }

    public string? DueRowKey { get; set; }

    public string? LockRowKey { get; set; }
}

internal sealed record AzureInboxIndexModel
{
    public string MessageId { get; init; } = string.Empty;
}
