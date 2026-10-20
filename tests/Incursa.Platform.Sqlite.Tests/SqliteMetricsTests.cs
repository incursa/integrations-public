// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Incursa.Platform;

namespace Incursa.Platform.Sqlite.Tests;

[Trait("Category", "Unit")]
public sealed class SqliteMetricsTests
{
    [Fact]
    public void ConnectionFactory_EmitsConnectionMetric()
    {
        var path = CreatePath();
        try
        {
            using var measurements = new MetricMeasurements();
            var options = new SqlitePlatformOptions { DataSource = path };
            using (new SqliteConnectionFactory(options).OpenConnection())
            {
            }

            measurements.Names.ShouldContain("incursa.platform.sqlite.connections.opened_total");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task MigrationAndIntegrityOperations_EmitMetrics()
    {
        var path = CreatePath();
        try
        {
            using var measurements = new MetricMeasurements();
            var options = new SqlitePlatformOptions { DataSource = path, ExpectedMigrationId = "001" };
            var factory = new SqliteConnectionFactory(options);
            var runner = new SqliteMigrationRunner(factory, options);
            await runner.ApplyAsync([new SqliteMigration("001", "CREATE TABLE metric_sample (Id INTEGER PRIMARY KEY);")], TestContext.Current.CancellationToken);
            var result = await new SqliteIntegrityChecker(factory, options).CheckAsync(TestContext.Current.CancellationToken);

            result.IsHealthy.ShouldBeTrue();
            measurements.Names.ShouldContain("incursa.platform.sqlite.migrations_applied_total");
            measurements.Names.ShouldContain("incursa.platform.sqlite.integrity_checks_total");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static string CreatePath() => Path.Combine(Path.GetTempPath(), $"incursa-metrics-{Guid.NewGuid():N}.sqlite");

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                File.Delete(candidate);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class MetricMeasurements : IDisposable
    {
        private readonly MeterListener listener;

        public MetricMeasurements()
        {
            Names = new ConcurrentBag<string>();
            listener = new MeterListener
            {
                InstrumentPublished = (instrument, meterListener) =>
                {
                    if (string.Equals(instrument.Meter.Name, "Incursa.Platform.Sqlite", StringComparison.Ordinal))
                    {
                        meterListener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            listener.SetMeasurementEventCallback<long>((instrument, _, _, _) => Names.Add(instrument.Name));
            listener.Start();
        }

        public ConcurrentBag<string> Names { get; }

        public void Dispose() => listener.Dispose();
    }
}
