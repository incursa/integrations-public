// Copyright (c) Incursa
// Licensed under the Apache License, Version 2.0.

namespace Incursa.Platform;

internal static class SqliteIdentifier
{
    public static void Validate(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !IsValid(value))
        {
            throw new ArgumentException("The SQLite identifier must contain only letters, digits, and underscores and must not start with a digit.", parameterName);
        }
    }

    private static bool IsValid(string value)
    {
        if (!(char.IsLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!(char.IsLetterOrDigit(value[index]) || value[index] == '_'))
            {
                return false;
            }
        }

        return true;
    }

    public static string Quote(string value)
    {
        Validate(value, nameof(value));
        return $"\"{value}\"";
    }
}
