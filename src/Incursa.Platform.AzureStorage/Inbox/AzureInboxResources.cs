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

using Microsoft.Extensions.Logging;

namespace Incursa.Platform;

internal sealed class AzureInboxResources
{
    public AzureInboxResources(
        AzurePlatformClientFactory clientFactory,
        AzurePlatformOptions options,
        AzurePlatformNameResolver nameResolver,
        AzurePlatformJsonSerializer serializer,
        ILoggerFactory loggerFactory)
    {
        Table = new AzurePlatformTable(
            clientFactory,
            options,
            loggerFactory.CreateLogger<AzureInboxResources>(),
            nameResolver.GetInboxTableName());
        PayloadStore = new AzurePlatformPayloadStore(
            options,
            serializer,
            new AzurePlatformBlobContainer(
                clientFactory,
                options,
                loggerFactory.CreateLogger<AzurePlatformBlobContainer>(),
                nameResolver.GetPayloadContainerName()),
            nameResolver);
        SignalQueue = new AzurePlatformQueue(
            clientFactory,
            options,
            serializer,
            loggerFactory.CreateLogger<AzurePlatformQueue>(),
            nameResolver.GetInboxSignalQueueName());
        Serializer = serializer;
    }

    public AzurePlatformTable Table { get; }

    public AzurePlatformPayloadStore PayloadStore { get; }

    public AzurePlatformQueue SignalQueue { get; }

    public AzurePlatformJsonSerializer Serializer { get; }
}
