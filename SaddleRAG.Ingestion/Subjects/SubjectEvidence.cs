// SubjectEvidence.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Validates that subject evidence quotes the current document rather than a catalog entry.</summary>
internal static class SubjectEvidence
{
    public static IReadOnlyDictionary<string, string> Sources(SubjectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        string[] fields =
        [
            descriptor.Title, descriptor.RelativePath, .. descriptor.Headings,
            descriptor.TableOfContents, descriptor.Summary, .. descriptor.StratifiedSections
        ];
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var excerpts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string field in fields)
        {
            string normalized = SubjectText.Normalize(field);
            for(var offset = 0; offset < normalized.Length; offset += SubjectClassificationLimits.MaxEvidenceCharacters)
            {
                string excerpt = normalized.Substring(offset,
                    Math.Min(SubjectClassificationLimits.MaxEvidenceCharacters, normalized.Length - offset)).Trim();
                if (excerpt.Length > 0 && excerpts.Add(excerpt))
                    result.Add($"source-{result.Count + 1}", excerpt);
            }
        }

        return result;
    }

    public static IReadOnlyList<string> Validate(IReadOnlyList<string>? quotations,
                                                 SubjectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (quotations is not { Count: > 0 })
            throw new InvalidDataException("Every subject requires supporting quotations from the current document.");
        if (quotations.Count > SubjectClassificationLimits.MaxEvidenceCount)
            throw new InvalidDataException($"Subject evidence is limited to {SubjectClassificationLimits.MaxEvidenceCount} entries.");

        string[] sourceFields =
        [
            descriptor.Title, descriptor.RelativePath, descriptor.TableOfContents, descriptor.Summary,
            .. descriptor.Headings, .. descriptor.StratifiedSections
        ];
        string[] normalizedFields = sourceFields.Select(SubjectText.Normalize).ToArray();
        IReadOnlyDictionary<string, string> sources = Sources(descriptor);
        var evidence = new List<string>(quotations.Count);
        var uniqueEvidence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string quotation in quotations)
        {
            string normalized = SubjectText.Normalize(quotation);
            // The model selects an excerpt; the application owns the exact source text.
            // Accept verified quotations as well for generators without schema support.
            if (sources.TryGetValue(normalized, out string? source))
                normalized = source;
            if (normalized.Length == 0 || normalized.Length > SubjectClassificationLimits.MaxEvidenceCharacters)
                throw new InvalidDataException("Subject evidence is empty or exceeds the configured bound.");
            if (!uniqueEvidence.Add(normalized))
                throw new InvalidDataException("Subject evidence entries must be unique.");
            if (!normalizedFields.Any(field => field.Contains(normalized, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Subject evidence must quote text from the current document descriptor. Do not paraphrase or quote catalog descriptions.");
            evidence.Add(normalized);
        }

        return evidence;
    }
}
