// SubjectSearchPolicy.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Deterministic subject resolution and inferred-query boosting policy.</summary>
public static class SubjectSearchPolicy
{
    public static string? ResolveExplicit(SubjectCatalogRecord catalog, string value)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrEmpty(value);
        string normalized = SubjectText.Normalize(value);
        var idMatches = catalog.Concepts
                               .Where(concept => string.Equals(concept.Id,
                                                               normalized,
                                                               StringComparison.OrdinalIgnoreCase))
                               .ToList();
        string? result;
        if (idMatches.Count == 1)
            result = idMatches[0].Id;
        else
        {
            var semanticMatches = catalog.Concepts
                                         .Where(concept => string.Equals(concept.Label,
                                                                         normalized,
                                                                         StringComparison.OrdinalIgnoreCase) ||
                                                           concept.Aliases.Any(alias => string.Equals(alias,
                                                                                                      normalized,
                                                                                                      StringComparison.OrdinalIgnoreCase)))
                                         .ToList();
            result = semanticMatches.Count switch
                {
                    0 => null,
                    1 => semanticMatches[0].Id,
                    var _ => throw new InvalidDataException($"Subject '{value}' is ambiguous in this library catalog.")
                };
        }

        return result;
    }

    public static string? Infer(SubjectCatalogRecord catalog, string query)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrEmpty(query);
        var matches = catalog.Concepts
                             .SelectMany(concept => concept.Aliases.Append(concept.Label)
                                                           .Select(term => new
                                                                               {
                                                                                   concept.Id,
                                                                                   Term = SubjectText.Normalize(term)
                                                                               }))
                             .Where(candidate => candidate.Term.Length > 0 && ContainsTerm(query, candidate.Term))
                             .OrderByDescending(candidate => candidate.Term.Length)
                             .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
                             .ToList();
        return matches.FirstOrDefault()?.Id;
    }

    public static double GetBoost(DocChunk chunk, string? inferredSubjectId)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return inferredSubjectId != null && chunk.SubjectIds.Contains(inferredSubjectId, StringComparer.Ordinal)
                   ? SubjectClassificationLimits.InferredSubjectBoost
                   : 0.0;
    }

    private static bool ContainsTerm(string query, string term)
    {
        int startIndex = 0;
        bool exhausted = false;
        bool result = false;
        while (startIndex < query.Length && !exhausted && !result)
        {
            int index = query.IndexOf(term, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                exhausted = true;
            else
            {
                int end = index + term.Length;
                bool beginsAtBoundary = index == 0 || !char.IsLetterOrDigit(query[index - 1]);
                bool endsAtBoundary = end == query.Length || !char.IsLetterOrDigit(query[end]);
                result = beginsAtBoundary && endsAtBoundary;
                startIndex = index + 1;
            }
        }

        return result;
    }
}
