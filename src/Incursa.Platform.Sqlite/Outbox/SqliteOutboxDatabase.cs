// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

using System.Data;
using System.Globalization;
using Incursa.Platform.Outbox;
using Microsoft.Data.Sqlite;

namespace Incursa.Platform;

internal static class SqliteOutboxDatabase
{
    internal static string GuidValue(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    internal static string? Timestamp(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset TimestampValue(object value) => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    internal static DateTimeOffset? NullableTimestampValue(object? value) => value is null || value is DBNull ? null : TimestampValue(value);

    internal static OutboxWorkItemIdentifier WorkItem(object value) => OutboxWorkItemIdentifier.From(Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!));

    internal static OutboxMessageIdentifier Message(object value) => OutboxMessageIdentifier.From(Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!));

    internal static JoinIdentifier Join(object value) => JoinIdentifier.From(Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!));

    internal static string Owner(OwnerToken value) => value.Value.ToString("D", CultureInfo.InvariantCulture);

    internal static void AddParameter(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    internal static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    internal static SqliteTransaction? AsSqliteTransaction(IDbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction as SqliteTransaction ?? throw new ArgumentException("The transaction must be a Microsoft.Data.Sqlite transaction.", nameof(transaction));
    }

    internal static async Task BeginImmediateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = Command(connection, "BEGIN IMMEDIATE;");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task CommitAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = Command(connection, "COMMIT;");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RollbackAsync(SqliteConnection connection)
    {
        using var command = Command(connection, "ROLLBACK;");
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
