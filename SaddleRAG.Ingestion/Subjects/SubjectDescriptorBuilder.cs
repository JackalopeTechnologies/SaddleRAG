// SubjectDescriptorBuilder.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Documents.Intake;

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Builds deterministic bounded subject descriptors after extraction.</summary>
public sealed class SubjectDescriptorBuilder
{
    public SubjectDescriptor Build(SourceDocumentRecord document,
                                   DocumentRevisionRecord revision,
                                   DocumentIntakeResult intake)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(intake);
        if (!intake.Succeeded)
            throw new InvalidOperationException("A subject descriptor cannot be built from a failed document intake.");
        if (!string.Equals(document.Id, revision.DocumentId, StringComparison.Ordinal))
            throw new ArgumentException("The revision does not belong to the supplied document.", nameof(revision));

        var orderedSections = intake.Sections.OrderBy(section => section.Order).ToList();
        var headings = orderedSections
                      .Select(section => SubjectText.Bounded(section.Title,
                                                            SubjectClassificationLimits.MaxHeadingCharacters))
                      .Where(heading => heading.Length > 0)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .Take(SubjectClassificationLimits.MaxHeadingCount)
                      .ToList();
        string tableOfContents = SubjectText.Bounded(string.Join('\n', headings),
                                                     SubjectClassificationLimits.MaxTableOfContentsCharacters);
        IReadOnlyList<string> stratifiedSections = BuildStratifiedSections(orderedSections);
        string summary = SubjectText.Bounded(string.Join(" ", stratifiedSections),
                                             SubjectClassificationLimits.MaxSummaryCharacters);
        string title = SubjectText.Bounded(intake.Title,
                                           SubjectClassificationLimits.MaxTitleCharacters);
        if (title.Length == 0)
            title = SubjectText.Bounded(document.DisplayName, SubjectClassificationLimits.MaxTitleCharacters);

        return new SubjectDescriptor
                   {
                       DocumentId = document.Id,
                       DocumentRevisionId = revision.Id,
                       Title = title,
                       RelativePath = SubjectText.Bounded(document.DisplayRelativePath,
                                                          SubjectClassificationLimits.MaxRelativePathCharacters),
                       Headings = headings,
                       TableOfContents = tableOfContents,
                       Summary = summary,
                       StratifiedSections = stratifiedSections
                   };
    }

    private static IReadOnlyList<string> BuildStratifiedSections(
        IReadOnlyList<DocumentIntakeSection> orderedSections)
    {
        IReadOnlyList<string> result;
        if (orderedSections.Count == 0)
            result = [];
        else
        {
            int sampleCount = Math.Min(orderedSections.Count,
                                       SubjectClassificationLimits.MaxStratifiedSectionCount);
            var samples = new List<string>(sampleCount);
            for(var i = 0; i < sampleCount; i++)
            {
                int index = sampleCount == 1
                                ? 0
                                : i * (orderedSections.Count - 1) / (sampleCount - 1);
                DocumentIntakeSection section = orderedSections[index];
                string sample = string.IsNullOrWhiteSpace(section.Title)
                                    ? section.Content
                                    : $"{section.Title}: {section.Content}";
                samples.Add(SubjectText.Bounded(sample,
                                                SubjectClassificationLimits.MaxStratifiedSectionCharacters));
            }

            result = samples;
        }

        return result;
    }
}
