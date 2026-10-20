// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Incursa.Platform.Audit;

namespace Incursa.Platform;

/// <summary>Options for audit events stored in the local SQLite database.</summary>
public sealed class SqliteAuditOptions
{
    /// <summary>Gets or sets the audit events table name.</summary>
    public string AuditEventsTable { get; set; } = "AuditEvents";

    /// <summary>Gets or sets the audit anchors table name.</summary>
    public string AuditAnchorsTable { get; set; } = "AuditAnchors";

    /// <summary>Gets or sets event validation options.</summary>
    public AuditValidationOptions ValidationOptions { get; set; } = new();

    /// <summary>Gets or sets whether the feature schema is created on first use.</summary>
    public bool EnableSchemaDeployment { get; set; } = true;

    internal void Validate()
    {
        SqliteIdentifier.Validate(AuditEventsTable, nameof(AuditEventsTable));
        SqliteIdentifier.Validate(AuditAnchorsTable, nameof(AuditAnchorsTable));
        ArgumentNullException.ThrowIfNull(ValidationOptions);
    }
}
