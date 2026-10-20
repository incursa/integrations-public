// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

internal static class SqlitePath
{
    public static string ValidateLocalFilePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A rooted local SQLite file path is required.", parameterName);
        }

        var trimmed = path.Trim();
        if (string.Equals(trimmed, ":memory:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("SQLite URI and :memory: data sources are not supported; use a rooted local file path.", parameterName);
        }

        // UNC paths and URI-like network paths are never valid for this provider.
        // A Unix mount point cannot be identified reliably from a path, so shared
        // mounts are prohibited by contract and must be avoided by the caller.
        if (trimmed.StartsWith("\\\\", StringComparison.Ordinal)
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.Contains("://", StringComparison.Ordinal))
        {
            throw new ArgumentException("SQLite must use a rooted local file path; UNC and network paths are not supported.", parameterName);
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new ArgumentException("SQLite must use a fully qualified local file path.", parameterName);
        }

        return Path.GetFullPath(trimmed);
    }
}
