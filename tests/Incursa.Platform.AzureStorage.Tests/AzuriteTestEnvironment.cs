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

using System.ComponentModel;
using System.Diagnostics;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;

namespace Incursa.Platform.AzureStorage.Tests;

internal sealed class AzuriteTestEnvironment
{
    private const string ConnectionStringEnvironmentVariable = "INCURSA_AZURE_STORAGE_CONNECTION_STRING";
    private const string EnableTablesEnvironmentVariable = "INCURSA_AZURE_STORAGE_ENABLE_TABLES";
    private const string DevelopmentStorageMarker = "UseDevelopmentStorage=true";
    private const string AzuriteImage = "mcr.microsoft.com/azure-storage/azurite";
    private const string AzuriteAccountName = "devstoreaccount1";
    private const string AzuriteAccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private static readonly SemaphoreSlim BootstrapLock = new(1, 1);

    private static AzuriteTestEnvironment? dockerEnvironment;
    private static string? dockerContainerName;
    private static string? dockerBootstrapSkipReason;
    private static bool cleanupRegistered;

    private AzuriteTestEnvironment(string connectionString, bool tablesEnabled)
    {
        ConnectionString = connectionString;
        TablesEnabled = tablesEnabled;
    }

    public string ConnectionString { get; }

    public bool TablesEnabled { get; }

    public static async Task<AzuriteTestEnvironment> GetBlobAndQueueAsync()
    {
        string? configuredConnectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return await GetOrStartDockerAsync().ConfigureAwait(false);
        }

        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(configuredConnectionString),
            $"Set {ConnectionStringEnvironmentVariable} to an Azure Storage or Azurite connection string to run Azure Storage integration tests.");

        string connectionString = configuredConnectionString!;
        try
        {
            await new BlobServiceClient(connectionString)
                .GetPropertiesAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
            await new QueueServiceClient(connectionString)
                .GetPropertiesAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Assert.Skip(
                $"Azure Storage blob/queue services are not available using {ConnectionStringEnvironmentVariable}: {exception.GetBaseException().Message}");
        }

        return new AzuriteTestEnvironment(connectionString, GetTablesEnabled(connectionString));
    }

    public static async Task<AzuriteTestEnvironment> GetTableAsync()
    {
        AzuriteTestEnvironment environment = await GetBlobAndQueueAsync().ConfigureAwait(false);
        Assert.SkipUnless(
            environment.TablesEnabled,
            $"Set {EnableTablesEnvironmentVariable}=true when using Azurite with table support enabled, or provide a real Azure Storage connection string.");

        string probeTableName = $"Probe{Guid.NewGuid():N}"[..17];
        try
        {
            TableClient tableClient = new TableServiceClient(environment.ConnectionString).GetTableClient(probeTableName);
            await tableClient.CreateIfNotExistsAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            await tableClient.DeleteAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Assert.Skip(
                $"Azure Storage table support is not available using {ConnectionStringEnvironmentVariable}: {exception.GetBaseException().Message}");
        }

        return environment;
    }

    private static bool GetTablesEnabled(string connectionString)
    {
        string? configuredValue = Environment.GetEnvironmentVariable(EnableTablesEnvironmentVariable);
        if (configuredValue is not null)
        {
            return configuredValue.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   configuredValue.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   configuredValue.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        return !IsAzuriteConnectionString(connectionString);
    }

    private static bool IsAzuriteConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return connectionString.Contains(DevelopmentStorageMarker, StringComparison.OrdinalIgnoreCase) ||
               connectionString.Contains("devstoreaccount1", StringComparison.OrdinalIgnoreCase) ||
               connectionString.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<AzuriteTestEnvironment> GetOrStartDockerAsync()
    {
        if (dockerEnvironment is not null)
        {
            return dockerEnvironment;
        }

        if (!string.IsNullOrWhiteSpace(dockerBootstrapSkipReason))
        {
            Assert.Skip(dockerBootstrapSkipReason);
        }

        await BootstrapLock.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        try
        {
            if (dockerEnvironment is not null)
            {
                return dockerEnvironment;
            }

            if (!string.IsNullOrWhiteSpace(dockerBootstrapSkipReason))
            {
                Assert.Skip(dockerBootstrapSkipReason);
            }

            string containerName = $"incursa-azurite-{Guid.NewGuid():N}";
            try
            {
                await RunDockerAsync(
                        "run",
                        "--detach",
                        "--rm",
                        "--name",
                        containerName,
                        "--publish",
                        "10000/tcp",
                        "--publish",
                        "10001/tcp",
                        "--publish",
                        "10002/tcp",
                        AzuriteImage,
                        "azurite",
                        "--blobHost",
                        "0.0.0.0",
                        "--queueHost",
                        "0.0.0.0",
                        "--tableHost",
                        "0.0.0.0",
                        "--skipApiVersionCheck")
                    .ConfigureAwait(false);

                string blobPort = await ResolveMappedPortAsync(containerName, 10000).ConfigureAwait(false);
                string queuePort = await ResolveMappedPortAsync(containerName, 10001).ConfigureAwait(false);
                string tablePort = await ResolveMappedPortAsync(containerName, 10002).ConfigureAwait(false);
                string connectionString = BuildAzuriteConnectionString(blobPort, queuePort, tablePort);

                await WaitForServicesAsync(connectionString).ConfigureAwait(false);

                dockerContainerName = containerName;
                dockerEnvironment = new AzuriteTestEnvironment(connectionString, tablesEnabled: true);
                RegisterCleanup();
                return dockerEnvironment;
            }
            catch (Exception exception)
            {
                await TryRemoveDockerContainerAsync(containerName).ConfigureAwait(false);
                dockerBootstrapSkipReason = $"Automatic Azurite Docker bootstrap failed: {exception.GetBaseException().Message}";
                Assert.Skip(dockerBootstrapSkipReason);
                throw;
            }
        }
        finally
        {
            BootstrapLock.Release();
        }
    }

    private static async Task WaitForServicesAsync(string connectionString)
    {
        DateTimeOffset timeoutAt = DateTimeOffset.UtcNow.AddMinutes(1);
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            try
            {
                await new BlobServiceClient(connectionString)
                    .GetPropertiesAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(false);
                await new QueueServiceClient(connectionString)
                    .GetPropertiesAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(false);

                string probeTableName = $"Probe{Guid.NewGuid():N}"[..17];
                TableClient tableClient = new TableServiceClient(connectionString).GetTableClient(probeTableName);
                await tableClient.CreateIfNotExistsAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
                await tableClient.DeleteAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
            {
                lastError = exception;
                await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"Azurite Docker container did not become ready before timeout. Last error: {lastError?.GetBaseException().Message}");
    }

    private static string BuildAzuriteConnectionString(string blobPort, string queuePort, string tablePort)
    {
        return System.FormattableString.Invariant(
            $"DefaultEndpointsProtocol=http;AccountName={AzuriteAccountName};AccountKey={AzuriteAccountKey};BlobEndpoint=http://127.0.0.1:{blobPort}/{AzuriteAccountName};QueueEndpoint=http://127.0.0.1:{queuePort}/{AzuriteAccountName};TableEndpoint=http://127.0.0.1:{tablePort}/{AzuriteAccountName};");
    }

    private static async Task<string> ResolveMappedPortAsync(string containerName, int containerPort)
    {
        string output = await RunDockerAsync("port", containerName, $"{containerPort}/tcp").ConfigureAwait(false);
        string line = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault()
            ?? throw new InvalidOperationException($"Docker did not report a mapped port for Azurite port {containerPort}.");

        int separatorIndex = line.LastIndexOf(':');
        if (separatorIndex < 0 || separatorIndex == line.Length - 1)
        {
            throw new InvalidOperationException($"Docker returned an unexpected port mapping '{line}'.");
        }

        return line[(separatorIndex + 1)..];
    }

    private static async Task<string> RunDockerAsync(params string[] arguments)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Docker did not start.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            throw new InvalidOperationException("Docker is required to auto-start Azurite when no storage connection string is configured.", exception);
        }

        string standardOutput = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        string standardError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {standardError.Trim()}");
        }

        return standardOutput.Trim();
    }

    private static void RegisterCleanup()
    {
        if (cleanupRegistered)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit += static (_, _) => CleanupDockerContainer();
        cleanupRegistered = true;
    }

    private static void CleanupDockerContainer()
    {
        if (string.IsNullOrWhiteSpace(dockerContainerName))
        {
            return;
        }

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("rm");
        process.StartInfo.ArgumentList.Add("--force");
        process.StartInfo.ArgumentList.Add(dockerContainerName);

        try
        {
            process.Start();
            process.WaitForExit(milliseconds: 5000);
        }
        catch
        {
        }
    }

    private static async Task TryRemoveDockerContainerAsync(string containerName)
    {
        try
        {
            await RunDockerAsync("rm", "--force", containerName).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
