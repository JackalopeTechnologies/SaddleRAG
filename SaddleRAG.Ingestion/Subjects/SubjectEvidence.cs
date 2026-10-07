// SubjectEvidence.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Validates that subject evidence quotes the current document rather than a catalog entry.</summary>
internal static class SubjectEvidence
{
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
        var evidence = new List<string>(quotations.Count);
        var uniqueEvidence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string quotation in quotations)
        {
            string normalized = SubjectText.Normalize(quotation);
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
