// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>Creates and restores tested SQLite online backups.</summary>
public sealed class SqliteBackupService
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly SqliteRetryPolicy retryPolicy;
    private readonly int busyTimeoutMilliseconds;

    /// <summary>Initializes a backup service.</summary>
    public SqliteBackupService(SqliteConnectionFactory connectionFactory, SqlitePlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.connectionFactory = connectionFactory;
        retryPolicy = new SqliteRetryPolicy(options);
        busyTimeoutMilliseconds = options.BusyTimeoutMilliseconds;
    }

    /// <summary>Backs up the active database with SQLite's online backup API.</summary>
    public async Task BackupAsync(string destinationPath, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var destination = SqlitePath.ValidateLocalFilePath(destinationPath, nameof(destinationPath));
        RejectLiveDatabaseOrSidecar(destination, nameof(destinationPath));

        if (File.Exists(destination) && !overwrite)
        {
            throw new IOException($"Backup destination already exists: {destination}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporaryDestination = CreateTemporarySibling(destination);
        try
        {
            await retryPolicy.ExecuteAsync(async ct =>
            {
                TryDelete(temporaryDestination);
                using var source = connectionFactory.OpenExistingConnection();
                CheckpointWal(source);
                EnsureHealthy(source);
                ct.ThrowIfCancellationRequested();
                using var target = OpenBackupTarget(temporaryDestination);
                await target.OpenAsync(ct).ConfigureAwait(false);
                SqliteConnectionFactory.ConfigureWritable(target, busyTimeoutMilliseconds);
                source.BackupDatabase(target);
                ct.ThrowIfCancellationRequested();
                CheckpointWal(target);
                EnsureHealthy(target);
                await target.CloseAsync().ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            AtomicReplace(temporaryDestination, destination);
        }
        finally
        {
            TryDelete(temporaryDestination);
            TryDelete(temporaryDestination + "-wal");
            TryDelete(temporaryDestination + "-shm");
        }
    }

    /// <summary>Validates a backup and atomically restores it to a sibling temporary file.</summary>
    public async Task RestoreAsync(string backupPath, string destinationPath, CancellationToken cancellationToken = default)
    {
        var backup = SqlitePath.ValidateLocalFilePath(backupPath, nameof(backupPath));
        var destination = SqlitePath.ValidateLocalFilePath(destinationPath, nameof(destinationPath));
        RejectLiveDatabaseOrSidecar(backup, nameof(backupPath));
        RejectLiveDatabaseOrSidecar(destination, nameof(destinationPath));
        if (!File.Exists(backup))
        {
            throw new FileNotFoundException("SQLite backup was not found.", backup);
        }

        if (PathsEqual(backup, destination))
        {
            throw new ArgumentException("Backup and restore destination must be different files.", nameof(destinationPath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporaryDestination = CreateTemporarySibling(destination);
        try
        {
            await retryPolicy.ExecuteAsync(async ct =>
            {
                TryDelete(temporaryDestination);
                using var source = connectionFactory.OpenConnectionAt(backup, SqliteOpenMode.ReadOnly);
                EnsureHealthy(source);
                ct.ThrowIfCancellationRequested();
                using var target = OpenBackupTarget(temporaryDestination);
                await target.OpenAsync(ct).ConfigureAwait(false);
                SqliteConnectionFactory.ConfigureWritable(target, busyTimeoutMilliseconds);
                source.BackupDatabase(target);
                ct.ThrowIfCancellationRequested();
                CheckpointWal(target);
                EnsureHealthy(target);
                await target.CloseAsync().ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            AtomicReplace(temporaryDestination, destination);
        }
        finally
        {
            TryDelete(temporaryDestination);
            TryDelete(temporaryDestination + "-wal");
            TryDelete(temporaryDestination + "-shm");
        }
    }

    private static SqliteConnection OpenBackupTarget(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        ForeignKeys = true,
        Pooling = false,
    }.ConnectionString);

    private static string CreateTemporarySibling(string destination) => $"{destination}.{Guid.NewGuid():N}.tmp";

    private static bool PathsEqual(string first, string second) =>
        string.Equals(first, second, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void RejectLiveDatabaseOrSidecar(string path, string parameterName)
    {
        var livePath = connectionFactory.FullDataSourcePath;
        if (PathsEqual(path, livePath)
            || PathsEqual(path, livePath + "-wal")
            || PathsEqual(path, livePath + "-shm")
            || PathsEqual(path, livePath + "-journal"))
        {
            throw new ArgumentException("Backup and restore paths cannot target the configured live database or its SQLite sidecar files.", parameterName);
        }
    }

    private static void CheckpointWal(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(FULL);";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != 0)
        {
            throw new SqliteException("SQLite could not checkpoint the WAL.", 5);
        }
    }

    private static void EnsureHealthy(SqliteConnection connection)
    {
        using var integrityCommand = connection.CreateCommand();
        integrityCommand.CommandText = "PRAGMA integrity_check;";
        var integrity = Convert.ToString(integrityCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SQLite backup failed integrity_check: '{integrity}'.");
        }

        using var foreignKeyCommand = connection.CreateCommand();
        foreignKeyCommand.CommandText = "PRAGMA foreign_key_check;";
        using var reader = foreignKeyCommand.ExecuteReader();
        if (reader.Read())
        {
            throw new InvalidDataException("SQLite backup failed foreign_key_check.");
        }
    }

    private static void AtomicReplace(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            try
            {
                File.Replace(temporaryPath, destinationPath, null, ignoreMetadataErrors: true);
            }
            catch (IOException)
            {
                File.Move(temporaryPath, destinationPath, overwrite: true);
            }
        }
        else
        {
            File.Move(temporaryPath, destinationPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
