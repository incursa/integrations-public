// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Incursa.Platform.Audit;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>Transactional SQLite audit event writer.</summary>
public sealed class SqliteAuditEventWriter : IAuditEventWriter
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;
    private readonly SqliteAuditOptions options;

    /// <summary>Initializes an audit writer over the supplied local SQLite store.</summary>
    public SqliteAuditEventWriter(SqliteConnectionFactory connectionFactory, SqliteRetryPolicy retryPolicy, SqliteAuditOptions? options = null)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        this.options = options ?? new SqliteAuditOptions();
        this.options.Validate();
    }

    /// <inheritdoc />
    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var validation = AuditEventValidator.Validate(auditEvent, options.ValidationOptions);
        if (!validation.IsValid) throw new ArgumentException(string.Join(" ", validation.Errors), nameof(auditEvent));
        await SqliteAuditSchema.EnsureAsync(connectionFactory, retryPolicy, options, cancellationToken).ConfigureAwait(false);
        await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            await SqliteAuditSchema.ExecuteAsync(connection, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                await SqliteAuditSchema.ExecuteAsync(connection, $"""
                    INSERT INTO {EventsTable} (AuditEventId,OccurredAtUtc,Name,DisplayMessage,Outcome,DataJson,ActorType,ActorId,ActorDisplay,CorrelationId,CausationId,TraceId,SpanId,CorrelationCreatedAtUtc,CorrelationTagsJson)
                    VALUES ($id,$occurred,$name,$display,$outcome,$data,$actorType,$actorId,$actorDisplay,$correlation,$causation,$trace,$span,$correlationCreated,$correlationTags);
                    """, ct, null, ("$id", auditEvent.EventId.Value), ("$occurred", SqliteAuditSchema.Utc(auditEvent.OccurredAtUtc)), ("$name", auditEvent.Name), ("$display", auditEvent.DisplayMessage), ("$outcome", (int)auditEvent.Outcome), ("$data", auditEvent.DataJson), ("$actorType", auditEvent.Actor?.ActorType), ("$actorId", auditEvent.Actor?.ActorId), ("$actorDisplay", auditEvent.Actor?.ActorDisplay), ("$correlation", auditEvent.Correlation?.CorrelationId.Value), ("$causation", auditEvent.Correlation?.CausationId?.Value), ("$trace", auditEvent.Correlation?.TraceId), ("$span", auditEvent.Correlation?.SpanId), ("$correlationCreated", auditEvent.Correlation is null ? null : SqliteAuditSchema.Utc(auditEvent.Correlation.CreatedAtUtc)), ("$correlationTags", SerializeTags(auditEvent.Correlation?.Tags))).ConfigureAwait(false);
                foreach (var anchor in auditEvent.Anchors)
                {
                    await SqliteAuditSchema.ExecuteAsync(connection, $"INSERT INTO {AnchorsTable}(AuditEventId,AnchorType,AnchorId,Role) VALUES ($event,$type,$id,$role);", ct, null, ("$event", auditEvent.EventId.Value), ("$type", anchor.AnchorType), ("$id", anchor.AnchorId), ("$role", anchor.Role)).ConfigureAwait(false);
                }

                await SqliteAuditSchema.ExecuteAsync(connection, "COMMIT;", ct).ConfigureAwait(false);
            }
            catch
            {
                try { await SqliteAuditSchema.ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); } catch (SqliteException) { }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
        SqlitePlatformMetrics.RecordAuditEventWritten();
    }

    private string EventsTable => SqliteIdentifier.Quote(options.AuditEventsTable);
    private string AnchorsTable => SqliteIdentifier.Quote(options.AuditAnchorsTable);
    private static string? SerializeTags(IReadOnlyDictionary<string, string>? tags) => tags is null ? null : JsonSerializer.Serialize(tags);
}
