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

internal static class TestWaiter
{
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);

        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(failureMessage);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None).ConfigureAwait(false);
        }
    }
}

internal sealed class CountingOutboxHandler : IOutboxHandler
{
    public CountingOutboxHandler(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        Topic = topic;
    }

    public string Topic { get; }

    public int Count => count;

    private int count;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        Interlocked.Increment(ref count);
        return Task.CompletedTask;
    }
}

internal sealed class FlakyInboxHandler : IInboxHandler
{
    public FlakyInboxHandler(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        Topic = topic;
    }

    public string Topic { get; }

    public int Attempts => attempts;

    private int attempts;

    public Task HandleAsync(InboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        int attempt = Interlocked.Increment(ref attempts);
        if (attempt == 1)
        {
            throw new InvalidOperationException("Transient inbox failure.");
        }

        return Task.CompletedTask;
    }
}

internal sealed class TestDbTransaction : System.Data.IDbTransaction
{
    public System.Data.IDbConnection? Connection => null;

    public System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.Unspecified;

    public void Commit()
    {
    }

    public void Dispose()
    {
    }

    public void Rollback()
    {
    }
}
