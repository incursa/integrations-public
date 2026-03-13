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

using Azure;
using Azure.Data.Tables;
using Cronos;
using Microsoft.Extensions.Logging;

namespace Incursa.Platform;

internal sealed partial class AzureSchedulerCore
{
    internal const byte StatusPending = 0;
    internal const byte StatusClaimed = 1;
    internal const byte StatusDone = 2;
    internal const byte StatusCancelled = 3;
    internal const byte StatusDispatching = 4;
    internal const string SignalName = "scheduler-ready";
    internal const string LeaseResourceName = "scheduler:run";

    private readonly AzureSchedulerResources resources;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AzureSchedulerCore> logger;

    public AzureSchedulerCore(
        AzureSchedulerResources resources,
        TimeProvider timeProvider,
        ILogger<AzureSchedulerCore> logger)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal AzureSchedulerResources Resources => resources;
}
