// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Ingestion.Documents.Intake;
using SaddleRAG.Ingestion.Subjects;

namespace SaddleRAG.Tests.Subjects;

public sealed class SubjectDescriptorTests
{
    [Fact]
    public void BuildSamplesBoundedEvidenceAcrossTheWholeDocument()
    {
        var sections = Enumerable.Range(0, 12)
                                 .Select(i => new DocumentIntakeSection(i,
                                                                        $"Heading {i}",
                                                                        $"SECTION-{i} " + new string((char)('a' + i), 1800),
                                                                        i + 1,
                                                                        i + 1))
                                 .ToList();
        var intake = SubjectTestData.Intake(sections, new string('T', 700));

        SubjectDescriptor descriptor = new SubjectDescriptorBuilder().Build(SubjectTestData.SourceDocument(),
                                                                              SubjectTestData.Revision(),
                                                                              intake);

        Assert.Equal(SubjectClassificationLimits.MaxTitleCharacters, descriptor.Title.Length);
        Assert.Equal("maintenance/hydraulics-safety.pdf", descriptor.RelativePath);
        Assert.InRange(descriptor.Headings.Count, 1, SubjectClassificationLimits.MaxHeadingCount);
        Assert.All(descriptor.Headings,
                   heading => Assert.InRange(heading.Length, 1, SubjectClassificationLimits.MaxHeadingCharacters));
        Assert.InRange(descriptor.TableOfContents.Length,
                       1,
                       SubjectClassificationLimits.MaxTableOfContentsCharacters);
        Assert.InRange(descriptor.Summary.Length, 1, SubjectClassificationLimits.MaxSummaryCharacters);
        Assert.InRange(descriptor.StratifiedSections.Count,
                       3,
                       SubjectClassificationLimits.MaxStratifiedSectionCount);
        Assert.All(descriptor.StratifiedSections,
                   sample => Assert.InRange(sample.Length,
                                            1,
                                            SubjectClassificationLimits.MaxStratifiedSectionCharacters));
        Assert.Contains("SECTION-0", descriptor.StratifiedSections[index: 0], StringComparison.Ordinal);
        Assert.Contains("SECTION-11", descriptor.StratifiedSections[^1], StringComparison.Ordinal);
        Assert.Contains(descriptor.StratifiedSections,
                        sample => sample.Contains("SECTION-5", StringComparison.Ordinal) ||
                                  sample.Contains("SECTION-6", StringComparison.Ordinal));
        Assert.DoesNotContain("E:\\", string.Join('\n', descriptor.StratifiedSections), StringComparison.OrdinalIgnoreCase);
    }
}
