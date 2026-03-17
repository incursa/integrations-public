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

using Incursa.Platform.Outbox;
using Shouldly;

namespace Incursa.Platform.AzureStorage.Tests;

[Trait("Category", "Integration")]
[Trait("Category", "Stress")]
public sealed class AzureConcurrencyStressIntegrationTests
{
    private static readonly AzureStressTestOptions StressOptions = AzureStressTestOptions.Load();

    [Fact]
    public async Task ConcurrentWebhookStyleIngress_CollapsesToSingleClaimableInboxMessagePerKey()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using AzurePlatformTestContext context = await AzurePlatformTestContext
            .CreateAsync(nameof(ConcurrentWebhookStyleIngress_CollapsesToSingleClaimableInboxMessagePerKey))
            .ConfigureAwait(false);

        const string topic = "tests.stress.webhook";
        const string source = "stress-provider";

        for (int round = 0; round < StressOptions.Rounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string messageId = $"webhook-{round:D4}";
            string payload = $$"""{"round":{{round}},"type":"webhook"}""";

            await RunConcurrentAsync(
                    StressOptions.ConcurrentAttempts,
                    (_, token) => RunWebhookIngressAsync(context.Inbox, topic, source, messageId, payload, token),
                    cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<InboxClaimResult> claims = await RunConcurrentAsync(
                    StressOptions.ClaimerAttempts,
                    (_, token) => ClaimInboxAsync(context.InboxWorkStore, token),
                    cancellationToken)
                .ConfigureAwait(false);

            claims.Sum(static result => result.MessageIds.Count).ShouldBe(
                1,
                $"Inbox stress round {round} expected exactly one claim after {StressOptions.ConcurrentAttempts} concurrent webhook ingress attempts.");

            InboxClaimResult winner = claims.Single(static result => result.MessageIds.Count == 1);
            winner.MessageIds.Single().ShouldBe(messageId);

            await context.InboxWorkStore.AckAsync(winner.OwnerToken, winner.MessageIds, cancellationToken).ConfigureAwait(false);
            (await context.Inbox.AlreadyProcessedAsync(messageId, source, cancellationToken).ConfigureAwait(false)).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task ConcurrentDeterministicOutboxEnqueue_CollapsesToSingleClaimableMessagePerKey()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using AzurePlatformTestContext context = await AzurePlatformTestContext
            .CreateAsync(nameof(ConcurrentDeterministicOutboxEnqueue_CollapsesToSingleClaimableMessagePerKey))
            .ConfigureAwait(false);

        const string topic = "tests.stress.outbox";

        for (int round = 0; round < StressOptions.Rounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string idempotencyKey = $"outbox-{round:D4}";
            string payload = $$"""{"round":{{round}},"type":"outbox"}""";

            await RunConcurrentAsync(
                    StressOptions.ConcurrentAttempts,
                    (_, token) => EnqueueDeterministicAsync(context.Outbox, idempotencyKey, topic, payload, token),
                    cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<OutboxClaimResult> claims = await RunConcurrentAsync(
                    StressOptions.ClaimerAttempts,
                    (_, token) => ClaimOutboxAsync(context.Outbox, token),
                    cancellationToken)
                .ConfigureAwait(false);

            claims.Sum(static result => result.MessageIds.Count).ShouldBe(
                1,
                $"Outbox stress round {round} expected exactly one claim after {StressOptions.ConcurrentAttempts} deterministic enqueue attempts.");

            OutboxClaimResult winner = claims.Single(static result => result.MessageIds.Count == 1);
            await context.Outbox.AckAsync(winner.OwnerToken, winner.MessageIds, cancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task ConcurrentLeaseAcquire_AllowsSingleWinnerPerResource()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using AzurePlatformTestContext context = await AzurePlatformTestContext
            .CreateAsync(nameof(ConcurrentLeaseAcquire_AllowsSingleWinnerPerResource))
            .ConfigureAwait(false);

        for (int round = 0; round < StressOptions.Rounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string resourceName = $"lease-{round:D4}";
            IReadOnlyList<ISystemLease?> leases = await RunConcurrentAsync(
                    StressOptions.ConcurrentAttempts,
                    (_, token) => context.LeaseFactory.AcquireAsync(resourceName, TimeSpan.FromSeconds(15), cancellationToken: token),
                    cancellationToken)
                .ConfigureAwait(false);

            List<ISystemLease> winners = leases.Where(static lease => lease is not null).Cast<ISystemLease>().ToList();
            winners.Count.ShouldBe(
                1,
                $"Lease stress round {round} expected exactly one owner after {StressOptions.ConcurrentAttempts} concurrent acquire attempts.");

            await winners[0].DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<bool> RunWebhookIngressAsync(
        IInbox inbox,
        string topic,
        string source,
        string messageId,
        string payload,
        CancellationToken cancellationToken)
    {
        bool duplicate = await inbox.AlreadyProcessedAsync(messageId, source, cancellationToken).ConfigureAwait(false);
        if (!duplicate)
        {
            await inbox.EnqueueAsync(topic, source, messageId, payload, cancellationToken).ConfigureAwait(false);
        }

        return duplicate;
    }

    private static async Task<bool> EnqueueDeterministicAsync(
        AzureOutboxService outbox,
        string idempotencyKey,
        string topic,
        string payload,
        CancellationToken cancellationToken)
    {
        await outbox.EnqueueDeterministicAsync(idempotencyKey, topic, payload, correlationId: null, dueTimeUtc: null, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static async Task<InboxClaimResult> ClaimInboxAsync(IInboxWorkStore workStore, CancellationToken cancellationToken)
    {
        OwnerToken ownerToken = OwnerToken.GenerateNew();
        IReadOnlyList<string> messageIds = await workStore.ClaimAsync(ownerToken, leaseSeconds: 30, batchSize: 1, cancellationToken).ConfigureAwait(false);
        return new InboxClaimResult(ownerToken, messageIds);
    }

    private static async Task<OutboxClaimResult> ClaimOutboxAsync(AzureOutboxService outbox, CancellationToken cancellationToken)
    {
        OwnerToken ownerToken = OwnerToken.GenerateNew();
        IReadOnlyList<OutboxWorkItemIdentifier> messageIds = await outbox.ClaimAsync(ownerToken, leaseSeconds: 30, batchSize: 1, cancellationToken).ConfigureAwait(false);
        return new OutboxClaimResult(ownerToken, messageIds);
    }

    private static async Task<IReadOnlyList<TResult>> RunConcurrentAsync<TResult>(
        int concurrentAttempts,
        Func<int, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrentAttempts);
        ArgumentNullException.ThrowIfNull(operation);

        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<TResult>[] tasks = Enumerable.Range(0, concurrentAttempts)
            .Select(
                index => Task.Run(
                    async () =>
                    {
                        await start.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                        return await operation(index, cancellationToken).ConfigureAwait(false);
                    },
                    cancellationToken))
            .ToArray();

        start.SetResult();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private sealed record InboxClaimResult(OwnerToken OwnerToken, IReadOnlyList<string> MessageIds);

    private sealed record OutboxClaimResult(OwnerToken OwnerToken, IReadOnlyList<OutboxWorkItemIdentifier> MessageIds);

    private sealed record AzureStressTestOptions(int Rounds, int ConcurrentAttempts, int ClaimerAttempts)
    {
        private const int DefaultRounds = 25;
        private const int DefaultConcurrentAttempts = 32;
        private const int DefaultClaimerAttempts = 8;
        private const string RoundsEnvironmentVariable = "INCURSA_AZURE_STORAGE_STRESS_ROUNDS";
        private const string ConcurrentAttemptsEnvironmentVariable = "INCURSA_AZURE_STORAGE_STRESS_CONCURRENCY";
        private const string ClaimerAttemptsEnvironmentVariable = "INCURSA_AZURE_STORAGE_STRESS_CLAIMERS";

        public static AzureStressTestOptions Load()
        {
            return new AzureStressTestOptions(
                ReadInt32(RoundsEnvironmentVariable, DefaultRounds, minimum: 1, maximum: 500),
                ReadInt32(ConcurrentAttemptsEnvironmentVariable, DefaultConcurrentAttempts, minimum: 2, maximum: 512),
                ReadInt32(ClaimerAttemptsEnvironmentVariable, DefaultClaimerAttempts, minimum: 2, maximum: 128));
        }

        private static int ReadInt32(string environmentVariableName, int defaultValue, int minimum, int maximum)
        {
            string? configuredValue = Environment.GetEnvironmentVariable(environmentVariableName);
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                return defaultValue;
            }

            if (!int.TryParse(configuredValue, provider: System.Globalization.CultureInfo.InvariantCulture, out int parsedValue))
            {
                throw new InvalidOperationException(
                    $"Environment variable {environmentVariableName} must be a whole number. Received '{configuredValue}'.");
            }

            return Math.Clamp(parsedValue, minimum, maximum);
        }
    }
}
