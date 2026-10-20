// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

/// <summary>A deterministic SQLite schema migration.</summary>
public sealed record SqliteMigration(string Id, string Sql)
{
    /// <summary>Validates this migration.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("Migration id is required.", nameof(Id));
        }

        if (string.IsNullOrWhiteSpace(Sql))
        {
            throw new ArgumentException("Migration SQL is required.", nameof(Sql));
        }

        if (ContainsTransactionControl(Sql))
        {
            throw new ArgumentException("Migration SQL must not contain transaction-control or VACUUM statements; the migration runner owns the transaction boundary.", nameof(Sql));
        }
    }

    private static bool ContainsTransactionControl(string sql)
    {
        var statementStart = true;
        var createTrigger = false;
        var triggerBody = false;
        var caseDepth = 0;
        var createPrefix = 0;
        var index = 0;
        while (index < sql.Length)
        {
            if (char.IsWhiteSpace(sql[index]) || sql[index] == ';')
            {
                if (sql[index] == ';' && !triggerBody)
                {
                    statementStart = true;
                    createTrigger = false;
                    createPrefix = 0;
                }

                index++;
                continue;
            }

            if (sql[index] == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index = sql.IndexOf('\n', index + 2);
                if (index < 0)
                {
                    return false;
                }

                continue;
            }

            if (sql[index] == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (index < 0)
                {
                    return false;
                }

                index += 2;
                continue;
            }

            if (sql[index] is '\'' or '"' or '`')
            {
                var delimiter = sql[index++];
                while (index < sql.Length)
                {
                    if (sql[index] != delimiter)
                    {
                        index++;
                        continue;
                    }

                    if (index + 1 < sql.Length && sql[index + 1] == delimiter)
                    {
                        index += 2;
                        continue;
                    }

                    index++;
                    break;
                }

                statementStart = false;
                continue;
            }

            if (sql[index] == '[')
            {
                index = sql.IndexOf(']', index + 1);
                index = index < 0 ? sql.Length : index + 1;
                statementStart = false;
                continue;
            }

            if (!statementStart)
            {
                if (triggerBody)
                {
                    var triggerTokenStart = index;
                    while (index < sql.Length && (char.IsLetter(sql[index]) || sql[index] == '_'))
                    {
                        index++;
                    }

                    if (triggerTokenStart == index)
                    {
                        index++;
                        continue;
                    }

                    var triggerToken = sql[triggerTokenStart..index];
                    if (triggerToken.Equals("CASE", StringComparison.OrdinalIgnoreCase))
                    {
                        caseDepth++;
                    }
                    else if (triggerToken.Equals("END", StringComparison.OrdinalIgnoreCase))
                    {
                        if (caseDepth > 0)
                        {
                            caseDepth--;
                        }
                        else
                        {
                            triggerBody = false;
                            createTrigger = false;
                        }
                    }

                    continue;
                }

                if (createTrigger && createPrefix > 0)
                {
                    var definitionTokenStart = index;
                    while (index < sql.Length && (char.IsLetter(sql[index]) || sql[index] == '_'))
                    {
                        index++;
                    }

                    if (definitionTokenStart == index)
                    {
                        index++;
                        continue;
                    }

                    var definitionToken = sql[definitionTokenStart..index];
                    if (definitionToken.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
                    {
                        triggerBody = true;
                        caseDepth = 0;
                    }
                }

                index++;
                continue;
            }

            var tokenStart = index;
            while (index < sql.Length && (char.IsLetter(sql[index]) || sql[index] == '_'))
            {
                index++;
            }

            if (tokenStart == index)
            {
                index++;
                continue;
            }

            var token = sql[tokenStart..index];
            if (token.Equals("BEGIN", StringComparison.OrdinalIgnoreCase)
                || token.Equals("COMMIT", StringComparison.OrdinalIgnoreCase)
                || token.Equals("END", StringComparison.OrdinalIgnoreCase)
                || token.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase)
                || token.Equals("SAVEPOINT", StringComparison.OrdinalIgnoreCase)
                || token.Equals("RELEASE", StringComparison.OrdinalIgnoreCase)
                || token.Equals("VACUUM", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            statementStart = false;
            createTrigger = token.Equals("CREATE", StringComparison.OrdinalIgnoreCase);
            createPrefix = createTrigger ? 1 : 0;

            while (createTrigger && index < sql.Length)
            {
                while (index < sql.Length && char.IsWhiteSpace(sql[index]))
                {
                    index++;
                }

                if (index >= sql.Length || sql[index] == ';' || sql[index] == '-' || sql[index] == '/')
                {
                    break;
                }

                if (sql[index] is '\'' or '"' or '`' or '[')
                {
                    break;
                }

                var nextTokenStart = index;
                while (index < sql.Length && (char.IsLetter(sql[index]) || sql[index] == '_'))
                {
                    index++;
                }

                if (nextTokenStart == index)
                {
                    break;
                }

                var nextToken = sql[nextTokenStart..index];
                if (nextToken.Equals("TEMP", StringComparison.OrdinalIgnoreCase)
                    || nextToken.Equals("TEMPORARY", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                createTrigger = nextToken.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase);
                createPrefix = createTrigger ? 2 : 0;
                break;
            }
        }

        return false;
    }
}
