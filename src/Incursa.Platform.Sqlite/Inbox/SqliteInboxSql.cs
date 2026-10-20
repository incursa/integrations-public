// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

internal static class SqliteInboxSql
{
    public static string Table(SqliteInboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return SqliteIdentifier.Quote(options.TableName);
    }

    public static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ReadTimestamp(object? value)
    {
        if (value is DateTimeOffset timestamp)
        {
            return timestamp.ToUniversalTime();
        }

        if (value is DateTime dateTime)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));
        }

        if (value is string text && DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException("The SQLite inbox contains an invalid UTC timestamp.");
    }

    public static DateTimeOffset? ReadNullableTimestamp(object? value)
    {
        return value is null || value is DBNull ? null : ReadTimestamp(value);
    }

    public static string Owner(OwnerToken ownerToken) =>
        ownerToken.Value.ToString("D", CultureInfo.InvariantCulture);

    public static void BeginImmediate(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE;";
        command.ExecuteNonQuery();
    }

    public static async Task BeginImmediateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task CommitAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "COMMIT;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task RollbackAsync(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ROLLBACK;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // The transaction may already have been rolled back by SQLite.
        }
    }

    public static SqliteParameter Parameter(string name, object? value) =>
        new(name, value ?? DBNull.Value);

    public static string BuildIdList(IReadOnlyList<string> ids, SqliteCommand command)
    {
        var names = new string[ids.Count];
        for (var index = 0; index < ids.Count; index++)
        {
            names[index] = "$id" + index.ToString(CultureInfo.InvariantCulture);
            command.Parameters.Add(Parameter(names[index], ids[index]));
        }

        return string.Join(", ", names);
    }
}
