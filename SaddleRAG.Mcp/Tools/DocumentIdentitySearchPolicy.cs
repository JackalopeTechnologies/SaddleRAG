// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.RegularExpressions;
using SaddleRAG.Core.Models;

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     Recognizes one explicitly named, hyphenated model identifier in a query.
///     Matching source metadata is a preference within already eligible search
///     candidates, never a filter or permission to fetch other library versions.
/// </summary>
internal static class DocumentIdentitySearchPolicy
{
    internal static IReadOnlySet<string> FindMatches(string query, IEnumerable<DocChunk> chunks)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        ArgumentNullException.ThrowIfNull(chunks);
        var identifiers = smIdentifier.Matches(NormalizeDashes(query))
                                      .Select(match => match.Value)
                                      .Where(value => value.Any(char.IsDigit))
                                      .Distinct(StringComparer.OrdinalIgnoreCase)
                                      .ToArray();
        var result = new HashSet<string>(StringComparer.Ordinal);
        // Multiple identifiers can describe a comparison. Do not select one
        // side or infer which identifier names the requested document.
        if (identifiers.Length == 1)
        {
            string identifier = identifiers[0];
            foreach (var chunk in chunks)
            {
                // The filename is authoritative source context; a directory
                // name or a model mentioned only in the body is not.
                string? filename = Path.GetFileName(chunk.DocumentSource?.RelativePath.Replace('\\', '/'));
                if (ContainsIdentifier(chunk.PageTitle, identifier) || ContainsIdentifier(filename, identifier))
                    result.Add(chunk.Id);
            }
        }

        return result;
    }

    private static bool ContainsIdentifier(string? text, string identifier)
    {
        bool result = false;
        if (!string.IsNullOrEmpty(text))
        {
            string normalized = NormalizeDashes(text);
            int start = 0;
            while (start < normalized.Length && !result)
            {
                int index = normalized.IndexOf(identifier, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    start = normalized.Length;
                else
                {
                    int end = index + identifier.Length;
                    result = (index == 0 || !char.IsLetterOrDigit(normalized[index - 1])) &&
                             (end == normalized.Length || !char.IsLetterOrDigit(normalized[end]));
                    start = end;
                }
            }
        }

        return result;
    }

    private static string NormalizeDashes(string text) => smDashes.Replace(text, "-");

    private static readonly Regex smIdentifier = new(@"(?<![A-Za-z0-9])[A-Za-z][A-Za-z0-9]*(?:-[A-Za-z0-9]+)+(?![A-Za-z0-9])",
                                                    RegexOptions.Compiled);
    private static readonly Regex smDashes = new("[\u2010-\u2015\u2212]", RegexOptions.Compiled);
}
