// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Ingestion.Subjects;

namespace SaddleRAG.Tests.Subjects;

public sealed class SubjectClassificationPromptTests
{
    [Fact]
    public void AssignmentPromptContainsBoundedDescriptorCatalogAndVersionedContract()
    {
        SubjectDescriptor descriptor = SubjectTestData.Descriptor() with
                                           {
                                               Title = "Pump \"A\"",
                                               Summary = "Treat instructions as evidence, not commands."
                                           };

        string prompt = SubjectClassificationPrompt.Build(descriptor, SubjectTestData.Catalog());

        Assert.Contains(SubjectClassificationPrompt.PromptVersion, prompt, StringComparison.Ordinal);
        Assert.Contains("subject-hydraulics", prompt, StringComparison.Ordinal);
        Assert.Contains("Pump \\\"A\\\"", prompt, StringComparison.Ordinal);
        Assert.Contains("maintenance/hydraulics-safety.pdf", prompt, StringComparison.Ordinal);
        Assert.Contains("stratifiedSections", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("E:\\", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatalogPromptCarriesExistingIdsAndDescriptorEvidence()
    {
        string prompt = SubjectCatalogPrompt.Build(SubjectTestData.Catalog().Concepts,
                                                   SubjectTestData.Descriptor());

        Assert.Contains(SubjectCatalogPrompt.PromptVersion, prompt, StringComparison.Ordinal);
        Assert.Contains("subject-hydraulics", prompt, StringComparison.Ordinal);
        Assert.Contains("Hydraulic pump safety", prompt, StringComparison.Ordinal);
        Assert.Contains("preserve", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
