// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Subjects;

internal static class SubjectText
{
    public static string Bounded(string? value, int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        string normalized = Normalize(value);
        return normalized.Length <= maximumCharacters ? normalized : normalized[..maximumCharacters].TrimEnd();
    }

    public static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null,
                                                       StringSplitOptions.RemoveEmptyEntries |
                                                       StringSplitOptions.TrimEntries));
}
