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

namespace Incursa.Platform.AzureStorage.Tests;

internal static class AzurePlatformTestOptions
{
    private const string DevelopmentStorageConnectionString = "UseDevelopmentStorage=true";

    public static AzurePlatformOptions CreateUnitOptions(Action<AzurePlatformOptions>? configure = null)
    {
        AzurePlatformOptions options = CreateOptions(DevelopmentStorageConnectionString, createResourcesIfMissing: false, nameof(CreateUnitOptions));
        configure?.Invoke(options);
        return options;
    }

    public static AzurePlatformOptions CreateIntegrationOptions(
        string connectionString,
        string scenarioName,
        Action<AzurePlatformOptions>? configure = null)
    {
        AzurePlatformOptions options = CreateOptions(connectionString, createResourcesIfMissing: true, scenarioName);
        configure?.Invoke(options);
        return options;
    }

    private static AzurePlatformOptions CreateOptions(
        string connectionString,
        bool createResourcesIfMissing,
        string scenarioName)
    {
        string token = Guid.NewGuid().ToString("N")[..12];
        string upperToken = token.ToUpperInvariant();
        string label = SanitizeLowercase(scenarioName, fallback: "case", maxLength: 10);

        return new AzurePlatformOptions
        {
            ConnectionString = connectionString,
            CreateResourcesIfMissing = createResourcesIfMissing,
            ResourcePrefix = $"Azp{upperToken[..4]}",
            EnvironmentName = $"T{upperToken[4..8]}",
            PayloadContainerName = $"azp-payload-{label}-{token}",
            OutboxSignalQueueName = $"azp-outbox-{label}-{token}",
            InboxSignalQueueName = $"azp-inbox-{label}-{token}",
            SchedulerSignalQueueName = $"azp-sched-{label}-{token}",
            OutboxTableName = $"AzpOutbox{upperToken}",
            InboxTableName = $"AzpInbox{upperToken}",
            SchedulerTableName = $"AzpSched{upperToken}",
            LeaseTableName = $"AzpLease{upperToken}",
            ExternalSideEffectTableName = $"AzpEffect{upperToken}",
            FanoutTableName = $"AzpFanout{upperToken}",
            WorkerIdleDelay = TimeSpan.FromMilliseconds(100),
            SchedulerMaxIdleDelay = TimeSpan.FromMilliseconds(250),
            ClaimLeaseDuration = TimeSpan.FromSeconds(2),
            CoordinationLeaseDuration = TimeSpan.FromSeconds(2),
        };
    }

    private static string SanitizeLowercase(string value, string fallback, int maxLength)
    {
        string sanitized = new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrEmpty(sanitized))
        {
            sanitized = fallback;
        }

        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength];
    }
}
