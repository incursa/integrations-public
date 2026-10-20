// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>Options for operation tracking in the local SQLite store.</summary>
public sealed class SqliteOperationOptions
{
    /// <summary>Gets or sets the operations table name.</summary>
    public string OperationsTable { get; set; } = "Operations";

    /// <summary>Gets or sets the operation events table name.</summary>
    public string OperationEventsTable { get; set; } = "OperationEvents";

    /// <summary>Gets or sets whether the feature schema is created on first use.</summary>
    public bool EnableSchemaDeployment { get; set; } = true;

    internal void Validate()
    {
        SqliteIdentifier.Validate(OperationsTable, nameof(OperationsTable));
        SqliteIdentifier.Validate(OperationEventsTable, nameof(OperationEventsTable));
    }
}
