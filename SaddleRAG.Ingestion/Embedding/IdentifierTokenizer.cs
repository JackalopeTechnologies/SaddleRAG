// IdentifierTokenizer.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.RegularExpressions;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Single source of truth for identifier-shape tokenization across
///     retrieval. PascalCase, dotted, ::-joined, snake_case, and arrow-
///     dereferenced identifiers all match here. Centralized so the BM25
///     indexer, BM25 query-side scorer, and the search-tools identifier
///     fast path stay aligned — a drift between any two of them
///     reintroduces the case-mismatch / split-on-separator bugs that
///     motivated the dual emission.
/// </summary>
public static class IdentifierTokenizer
{
    /// <summary>
    ///     Match every identifier-shaped token in <paramref name="input" />
    ///     whose length meets <paramref name="minLength" />. Returns raw
    ///     matches with original casing.
    /// </summary>
    public static IEnumerable<string> Matches(string input, int minLength)
    {
        ArgumentException.ThrowIfNullOrEmpty(input);
        if (minLength < 1)
            throw new ArgumentOutOfRangeException(nameof(minLength), minLength, "minLength must be >= 1");

        var result = smIdentifierTokenRegex.Matches(input)
                                           .Where(m => m.Value.Length >= minLength)
                                           .Select(m => m.Value);
        return result;
    }

    /// <summary>
    ///     Emit each identifier token in both its original case AND a
    ///     lowercase variant (when the two differ). Used by the BM25 index
    ///     builder and query scorer so a case-mismatched dotted query like
    ///     <c>axisfault.disabled</c> still matches an indexed
    ///     <c>AxisFault.Disabled</c> — the prose tokenizer can't bridge
    ///     that gap because it splits at the separators.
    /// </summary>
    public static IEnumerable<string> EmitRawAndLowercase(string input, int minLength)
    {
        ArgumentException.ThrowIfNullOrEmpty(input);
        if (minLength < 1)
            throw new ArgumentOutOfRangeException(nameof(minLength), minLength, "minLength must be >= 1");

        return EmitRawAndLowercaseCore(input, minLength);
    }

    private static IEnumerable<string> EmitRawAndLowercaseCore(string input, int minLength)
    {
        foreach(var raw in Matches(input, minLength))
        {
            yield return raw;
            var lower = raw.ToLowerInvariant();
            if (!string.Equals(lower, raw, StringComparison.Ordinal))
                yield return lower;
        }
    }

    /// <summary>
    ///     Extract identifier-shaped tokens, deduped with ordinal
    ///     comparison. Original casing is preserved because the rerank
    ///     fast-path uses the result to look up <c>QualifiedName</c>
    ///     case-insensitively — feeding it lowercased tokens loses the
    ///     ability to match exact-case indexes first.
    /// </summary>
    public static IReadOnlyList<string> ExtractDistinct(string input, int minLength)
    {
        ArgumentException.ThrowIfNullOrEmpty(input);
        if (minLength < 1)
            throw new ArgumentOutOfRangeException(nameof(minLength), minLength, "minLength must be >= 1");

        var tokens = Matches(input, minLength).Distinct(StringComparer.Ordinal).ToList();
        return tokens;
    }

    // Compiled regex backing every consumer. PascalCase, dotted, ::-joined,
    // snake_case, and arrow-dereferenced segments.
    private static readonly Regex smIdentifierTokenRegex =
        new Regex(@"[A-Za-z_][A-Za-z0-9_]*(?:(?:\.|::|->)[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.Compiled);
}
