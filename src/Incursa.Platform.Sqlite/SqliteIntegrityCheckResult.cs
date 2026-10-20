// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>Result of SQLite integrity, foreign-key, and schema-version checks.</summary>
public sealed record SqliteIntegrityCheckResult(
    string IntegrityResult,
    int ForeignKeyViolationCount,
    string? AppliedMigrationId,
    string? ExpectedMigrationId)
{
    /// <summary>Gets whether all checks passed.</summary>
    public bool IsHealthy =>
        string.Equals(IntegrityResult, "ok", StringComparison.OrdinalIgnoreCase)
        && ForeignKeyViolationCount == 0
        && (ExpectedMigrationId is null || string.Equals(AppliedMigrationId, ExpectedMigrationId, StringComparison.Ordinal));
}
