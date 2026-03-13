// Copyright (c) Incursa
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Incursa.Platform;

internal static class AzurePlatformFailureText
{
    private const int MaxLength = 2048;
    private const string FallbackMessage = "Failure details unavailable.";
    private const string TruncatedSuffix = "... [truncated]";

    public static string FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string message = $"{exception.GetType().Name}: {exception.Message}";
        if (exception.InnerException is not null)
        {
            message = string.Concat(
                message,
                " | Inner: ",
                exception.InnerException.GetType().Name,
                ": ",
                exception.InnerException.Message);
        }

        return Normalize(message);
    }

    public static string Normalize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return FallbackMessage;
        }

        string trimmed = message.Trim();
        if (trimmed.Length <= MaxLength)
        {
            return trimmed;
        }

        int maxPrefixLength = MaxLength - TruncatedSuffix.Length;
        if (maxPrefixLength <= 0)
        {
            return TruncatedSuffix;
        }

        return string.Concat(trimmed.AsSpan(0, maxPrefixLength), TruncatedSuffix);
    }
}
