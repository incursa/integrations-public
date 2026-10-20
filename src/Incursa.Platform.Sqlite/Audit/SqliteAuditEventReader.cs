// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Incursa.Platform.Audit;
using Incursa.Platform.Correlation;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>SQLite audit event reader with time, name, and anchor filters.</summary>
public sealed class SqliteAuditEventReader : IAuditEventReader
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;
    private readonly SqliteAuditOptions options;

    /// <summary>Initializes an audit reader over the supplied local SQLite store.</summary>
    public SqliteAuditEventReader(SqliteConnectionFactory connectionFactory, SqliteRetryPolicy retryPolicy, SqliteAuditOptions? options = null)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        this.options = options ?? new SqliteAuditOptions();
        this.options.Validate();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SqliteAuditSchema.EnsureAsync(connectionFactory, retryPolicy, options, cancellationToken).ConfigureAwait(false);
        return await retryPolicy.ExecuteAsync(async ct =>
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            var clauses = new List<string>();
            if (query.Anchors.Count > 0)
            {
                clauses.Add($"EXISTS (SELECT 1 FROM {AnchorsTable} a WHERE a.AuditEventId=e.AuditEventId AND (" + string.Join(" OR ", query.Anchors.Select((_, i) => $"(a.AnchorType=$anchorType{i} AND a.AnchorId=$anchorId{i} AND a.Role=$anchorRole{i})")) + "))");
                for (var i = 0; i < query.Anchors.Count; i++)
                {
                    command.Parameters.AddWithValue($"$anchorType{i}", query.Anchors[i].AnchorType);
                    command.Parameters.AddWithValue($"$anchorId{i}", query.Anchors[i].AnchorId);
                    command.Parameters.AddWithValue($"$anchorRole{i}", query.Anchors[i].Role);
                }
            }

            if (query.FromUtc is not null) { clauses.Add("e.OccurredAtUtc >= $from"); command.Parameters.AddWithValue("$from", SqliteAuditSchema.Utc(query.FromUtc.Value)); }
            if (query.ToUtc is not null) { clauses.Add("e.OccurredAtUtc <= $to"); command.Parameters.AddWithValue("$to", SqliteAuditSchema.Utc(query.ToUtc.Value)); }
            if (!string.IsNullOrWhiteSpace(query.Name)) { clauses.Add("e.Name = $name"); command.Parameters.AddWithValue("$name", query.Name); }
            command.CommandText = $"SELECT e.AuditEventId,e.OccurredAtUtc,e.Name,e.DisplayMessage,e.Outcome,e.DataJson,e.ActorType,e.ActorId,e.ActorDisplay,e.CorrelationId,e.CausationId,e.TraceId,e.SpanId,e.CorrelationCreatedAtUtc,e.CorrelationTagsJson FROM {EventsTable} e" + (clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses)) + " ORDER BY e.OccurredAtUtc DESC" + (query.Limit is null ? ";" : " LIMIT $limit;");
            if (query.Limit is not null) command.Parameters.AddWithValue("$limit", query.Limit.Value);

            var rows = new List<Row>();
            using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(ReadRow(reader));
            }

            var result = new List<AuditEvent>(rows.Count);
            foreach (var row in rows)
            {
                using var anchorsCommand = connection.CreateCommand();
                anchorsCommand.CommandText = $"SELECT AnchorType,AnchorId,Role FROM {AnchorsTable} WHERE AuditEventId=$id ORDER BY rowid;";
                anchorsCommand.Parameters.AddWithValue("$id", row.Id);
                var anchors = new List<EventAnchor>();
                using var anchorsReader = await anchorsCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await anchorsReader.ReadAsync(ct).ConfigureAwait(false)) anchors.Add(new EventAnchor(anchorsReader.GetString(0), anchorsReader.GetString(1), anchorsReader.GetString(2)));
                result.Add(Map(row, anchors));
            }

            return (IReadOnlyList<AuditEvent>)result;
        }, cancellationToken).ConfigureAwait(false);
    }

    private string EventsTable => SqliteIdentifier.Quote(options.AuditEventsTable);
    private string AnchorsTable => SqliteIdentifier.Quote(options.AuditAnchorsTable);

    private static Row ReadRow(SqliteDataReader reader) => new(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(), reader.GetString(2), reader.GetString(3), (EventOutcome)reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(), reader.IsDBNull(14) ? null : reader.GetString(14));

    private static AuditEvent Map(Row row, IReadOnlyList<EventAnchor> anchors)
    {
        var actor = row.ActorType is null || row.ActorId is null ? null : new AuditActor(row.ActorType, row.ActorId, row.ActorDisplay);
        CorrelationContext? correlation = null;
        if (row.CorrelationId is not null) correlation = new CorrelationContext(new CorrelationId(row.CorrelationId), row.CausationId is null ? null : new CorrelationId(row.CausationId), row.TraceId, row.SpanId, row.CorrelationCreatedAtUtc ?? DateTimeOffset.UnixEpoch, DeserializeTags(row.CorrelationTagsJson));
        return new AuditEvent(new AuditEventId(row.Id), row.OccurredAtUtc, row.Name, row.DisplayMessage, row.Outcome, anchors, row.DataJson, actor, correlation);
    }

    private static Dictionary<string, string>? DeserializeTags(string? json) => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(json);

    private sealed record Row(string Id, DateTimeOffset OccurredAtUtc, string Name, string DisplayMessage, EventOutcome Outcome, string? DataJson, string? ActorType, string? ActorId, string? ActorDisplay, string? CorrelationId, string? CausationId, string? TraceId, string? SpanId, DateTimeOffset? CorrelationCreatedAtUtc, string? CorrelationTagsJson);
}
