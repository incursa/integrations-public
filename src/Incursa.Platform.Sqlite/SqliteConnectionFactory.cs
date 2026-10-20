// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

/// <summary>Creates SQLite connections with provider-required durability settings.</summary>
public sealed class SqliteConnectionFactory
{
    private readonly SqlitePlatformOptions options;

    /// <summary>Initializes a factory with validated options.</summary>
    public SqliteConnectionFactory(SqlitePlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
    }

    /// <summary>Gets the configured data source.</summary>
    public string DataSource => options.DataSource;

    /// <summary>Gets the normalized, fully qualified local database file path.</summary>
    public string FullDataSourcePath => SqlitePath.ValidateLocalFilePath(options.DataSource, nameof(options.DataSource));

    /// <summary>Opens and configures a durable SQLite connection.</summary>
    public SqliteConnection OpenConnection()
    {
        return OpenConnectionCore(FullDataSourcePath, SqliteOpenMode.ReadWriteCreate, configureWritable: true);
    }

    internal SqliteConnection OpenExistingConnection()
    {
        return OpenConnectionCore(FullDataSourcePath, SqliteOpenMode.ReadWrite, configureWritable: true);
    }

    internal SqliteConnection OpenConnectionAt(string path, SqliteOpenMode mode)
    {
        var fullPath = SqlitePath.ValidateLocalFilePath(path, nameof(path));
        return OpenConnectionCore(fullPath, mode, configureWritable: mode != SqliteOpenMode.ReadOnly);
    }

    private SqliteConnection OpenConnectionCore(string path, SqliteOpenMode mode, bool configureWritable)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            // WAL supports concurrent connections without SQLite shared-cache mode,
            // whose process-global schema state can race during simultaneous setup.
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeoutMilliseconds / 1_000d)),
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            if (configureWritable)
            {
                ConfigureWritable(connection, options.BusyTimeoutMilliseconds);
            }

            SqlitePlatformMetrics.RecordConnectionOpened(configureWritable ? "read-write" : "read-only");
            return connection;
        }
        catch
        {
            SqlitePlatformMetrics.RecordConnectionOpenFailure();
            connection.Dispose();
            throw;
        }
    }

    internal static void ConfigureWritable(SqliteConnection connection, int busyTimeoutMilliseconds = 5_000)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using (var busy = connection.CreateCommand())
        {
            busy.CommandText = $"PRAGMA busy_timeout={busyTimeoutMilliseconds};";
            busy.ExecuteNonQuery();
        }

        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys=ON;";
            foreignKeys.ExecuteNonQuery();
        }

        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = Convert.ToString(journal.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"SQLite did not enable WAL mode; reported mode '{mode}'.");
            }
        }

        using (var synchronous = connection.CreateCommand())
        {
            synchronous.CommandText = "PRAGMA synchronous=FULL;";
            synchronous.ExecuteNonQuery();
        }

        // Query each setting separately because SQLite returns one row per PRAGMA.
        using var modeCommand = connection.CreateCommand();
        modeCommand.CommandText = "PRAGMA journal_mode;";
        if (!string.Equals(Convert.ToString(modeCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture), "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SQLite WAL mode validation failed.");
        }

        using var syncCommand = connection.CreateCommand();
        syncCommand.CommandText = "PRAGMA synchronous;";
        if (Convert.ToInt32(syncCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 2)
        {
            throw new InvalidOperationException("SQLite synchronous mode validation failed; FULL is required.");
        }

        using var fkCommand = connection.CreateCommand();
        fkCommand.CommandText = "PRAGMA foreign_keys;";
        if (Convert.ToInt32(fkCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException("SQLite foreign-key enforcement validation failed.");
        }

        using var timeoutCommand = connection.CreateCommand();
        timeoutCommand.CommandText = "PRAGMA busy_timeout;";
        if (Convert.ToInt32(timeoutCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != busyTimeoutMilliseconds)
        {
            throw new InvalidOperationException("SQLite busy-timeout validation failed.");
        }
    }
}
