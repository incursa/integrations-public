// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform;
using Incursa.Platform.Audit;
using Incursa.Platform.Correlation;

namespace Incursa.Platform.Sqlite.Tests;

[Trait("Category", "Unit")]
public sealed class SqliteAuditTests
{
    [Fact]
    public async Task WriterAndReader_RoundTripAndApplyFilters()
    {
        using var database = new TemporaryDatabase();
        var writer = database.CreateWriter();
        var reader = database.CreateReader();
        var occurredAt = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var correlation = new CorrelationContext(new CorrelationId("corr-audit"), null, "trace", "span", occurredAt, new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = "t-1" });
        var auditEvent = new AuditEvent(AuditEventId.NewId(), occurredAt, "invoice.created", "Invoice created", EventOutcome.Success, new[] { new EventAnchor("Invoice", "INV-1", "Subject"), new EventAnchor("Tenant", "t-1", "Owner") }, "{\"id\":\"INV-1\"}", new AuditActor("System", "svc", "Service"), correlation);
        await writer.WriteAsync(auditEvent, TestContext.Current.CancellationToken);

        var byAnchor = await reader.QueryAsync(new AuditQuery(new[] { new EventAnchor("Invoice", "INV-1", "Subject") }, occurredAt.AddMinutes(-1), occurredAt.AddMinutes(1), "invoice.created", 10), TestContext.Current.CancellationToken);
        byAnchor.Count.ShouldBe(1);
        byAnchor[0].EventId.ShouldBe(auditEvent.EventId);
        byAnchor[0].Anchors.Count.ShouldBe(2);
        byAnchor[0].Actor!.ActorId.ShouldBe("svc");
        byAnchor[0].Correlation!.Tags!["tenant"].ShouldBe("t-1");

        var byOtherAnchor = await reader.QueryAsync(new AuditQuery(new[] { new EventAnchor("Tenant", "t-1", "Owner") }), TestContext.Current.CancellationToken);
        byOtherAnchor.Count.ShouldBe(1);
        (await reader.QueryAsync(new AuditQuery(new[] { new EventAnchor("Invoice", "INV-2", "Subject") }), TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await reader.QueryAsync(new AuditQuery(Array.Empty<EventAnchor>(), name: "missing"), TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Writer_RollsBackEventWhenAnchorWriteFails()
    {
        using var database = new TemporaryDatabase();
        var writer = database.CreateWriter();
        var eventId = AuditEventId.NewId();
        var duplicateAnchors = new[] { new EventAnchor("Invoice", "INV-1", "Subject"), new EventAnchor("Invoice", "INV-1", "Subject") };
        var auditEvent = new AuditEvent(eventId, DateTimeOffset.UtcNow, "invoice.failed", "Invoice failed", EventOutcome.Failure, duplicateAnchors);

        await Should.ThrowAsync<Exception>(() => writer.WriteAsync(auditEvent, TestContext.Current.CancellationToken));
        using var connection = database.Factory.OpenConnection();
        ScalarInt(connection, "SELECT COUNT(*) FROM AuditEvents WHERE AuditEventId=$id", ("$id", eventId.Value)).ShouldBe(0);
        ScalarInt(connection, "SELECT COUNT(*) FROM AuditAnchors WHERE AuditEventId=$id", ("$id", eventId.Value)).ShouldBe(0);
    }

    [Fact]
    public async Task Writer_RejectsInvalidPayloadBeforePersisting()
    {
        using var database = new TemporaryDatabase();
        var options = new SqliteAuditOptions { ValidationOptions = new AuditValidationOptions { MaxDataJsonLength = 4 } };
        var writer = new SqliteAuditEventWriter(database.Factory, database.RetryPolicy, options);
        var auditEvent = new AuditEvent(AuditEventId.NewId(), DateTimeOffset.UtcNow, "payload.large", "Payload", EventOutcome.Success, Array.Empty<EventAnchor>(), "12345");

        await Should.ThrowAsync<ArgumentException>(() => writer.WriteAsync(auditEvent, TestContext.Current.CancellationToken));
        File.Exists(database.Options.DataSource).ShouldBeFalse();
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "incursa-sqlite-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            Options = new SqlitePlatformOptions { DataSource = Path.Combine(DirectoryPath, "database.sqlite"), BusyTimeoutMilliseconds = 1_000, MaxRetryCount = 20, RetryBaseDelay = TimeSpan.FromMilliseconds(1) };
            Factory = new SqliteConnectionFactory(Options);
            RetryPolicy = new SqliteRetryPolicy(Options);
        }

        public string DirectoryPath { get; }
        public SqlitePlatformOptions Options { get; }
        public SqliteConnectionFactory Factory { get; }
        public SqliteRetryPolicy RetryPolicy { get; }

        public SqliteAuditEventWriter CreateWriter() => new(Factory, RetryPolicy);
        public SqliteAuditEventReader CreateReader() => new(Factory, RetryPolicy);

        public void Dispose()
        {
            try { Directory.Delete(DirectoryPath, recursive: true); } catch (IOException) { }
        }
    }

    private static int ScalarInt(Microsoft.Data.Sqlite.SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
