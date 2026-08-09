// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Subjects;

namespace SaddleRAG.Tests.Subjects;

public sealed class SubjectClassifierTests
{
    [Theory]
    [InlineData("```json\n{\"primary\":{\"subjectId\":\"subject-hydraulics\",\"confidence\":0.9,\"evidence\":[\"pump\"]},\"secondary\":[]}\n```")]
    [InlineData("<json>{\"primary\":{\"subjectId\":\"subject-hydraulics\",\"confidence\":0.9,\"evidence\":[\"pump\"]},\"secondary\":[]}</json>")]
    public async Task KnownJsonFramingFromGeneratorPersistsAssignment(string response)
    {
        var repository = new InMemorySubjectAssignmentRepository();
        var classifier = new SubjectClassifier(new ScriptedSubjectGenerator(response),
                                               new FixedSubjectTimeProvider());

        SubjectAssignmentRecord result = await classifier.ClassifyAsync(
                                               repository,
                                               SubjectTestData.Descriptor(),
                                               SubjectTestData.Catalog(),
                                               "2026-08-04",
                                               "scan-framed",
                                               TestContext.Current.CancellationToken);

        Assert.Equal("subject-hydraulics", result.Primary.SubjectId);
        Assert.Same(result, Assert.Single(repository.Persisted));
    }

    [Fact]
    public async Task PersistsPrimarySecondaryEvidenceAndFullProvenance()
    {
        var generator = new ScriptedSubjectGenerator(
            """
            {"primary":{"subjectId":"subject-hydraulics","confidence":0.92,"evidence":["pump service"]},
             "secondary":[{"subjectId":"subject-safety","confidence":0.76,"evidence":["lockout stored energy"]}]}
            """);
        var repository = new InMemorySubjectAssignmentRepository();
        var classifier = new SubjectClassifier(generator, new FixedSubjectTimeProvider());

        SubjectAssignmentRecord result = await classifier.ClassifyAsync(repository,
                                                                         SubjectTestData.Descriptor(),
                                                                         SubjectTestData.Catalog(),
                                                                         "2026-08-04",
                                                                         "scan-1",
                                                                         TestContext.Current.CancellationToken);

        Assert.Equal("subject-hydraulics", result.Primary.SubjectId);
        Assert.Equal(0.92f, result.Primary.Confidence);
        Assert.Equal(["pump service"], result.Primary.Evidence);
        SubjectSelection secondary = Assert.Single(result.Secondary);
        Assert.Equal("subject-safety", secondary.SubjectId);
        Assert.Equal(["lockout stored energy"], secondary.Evidence);
        Assert.False(result.NeedsReview);
        Assert.Equal("taxonomy-000001", result.TaxonomyVersion);
        Assert.Equal("scripted", result.Provenance.Backend);
        Assert.Equal("scripted-subject-model", result.Provenance.ModelId);
        Assert.Equal(SubjectClassificationPrompt.PromptVersion, result.Provenance.PromptVersion);
        Assert.Equal(SubjectTestData.GeneratedAtUtc, result.Provenance.GeneratedAtUtc);
        Assert.Same(result, Assert.Single(repository.Persisted));
    }

    [Fact]
    public async Task UnknownSubjectIdRetriesAgainstTheSameCatalogBeforePersistence()
    {
        var generator = new ScriptedSubjectGenerator(
            """
            {"primary":{"subjectId":"subject-hallucinated","confidence":0.9,"evidence":["pump"]},"secondary":[]}
            """,
            """
            {"primary":{"subjectId":"subject-hydraulics","confidence":0.9,"evidence":["pump"]},"secondary":[]}
            """);
        var repository = new InMemorySubjectAssignmentRepository();
        var classifier = new SubjectClassifier(generator, new FixedSubjectTimeProvider());

        SubjectAssignmentRecord result = await classifier.ClassifyAsync(repository,
                                                                         SubjectTestData.Descriptor(),
                                                                         SubjectTestData.Catalog(),
                                                                         "2026-08-04",
                                                                         "scan-retry-unknown-id",
                                                                         TestContext.Current.CancellationToken);

        Assert.Equal("subject-hydraulics", result.Primary.SubjectId);
        Assert.Equal(2, generator.Prompts.Count);
        Assert.Same(result, Assert.Single(repository.Persisted));
    }

    [Fact]
    public async Task NullSecondarySelectionRetriesBeforePersistence()
    {
        var generator = new ScriptedSubjectGenerator(
            """
            {"primary":{"subjectId":"subject-hydraulics","confidence":0.9,"evidence":["pump"]},"secondary":[null]}
            """,
            """
            {"primary":{"subjectId":"subject-hydraulics","confidence":0.9,"evidence":["pump"]},"secondary":[]}
            """);
        var repository = new InMemorySubjectAssignmentRepository();
        var classifier = new SubjectClassifier(generator, new FixedSubjectTimeProvider());

        SubjectAssignmentRecord result = await classifier.ClassifyAsync(repository,
                                                                         SubjectTestData.Descriptor(),
                                                                         SubjectTestData.Catalog(),
                                                                         "2026-08-04",
                                                                         "scan-retry-null-selection",
                                                                         TestContext.Current.CancellationToken);

        Assert.Empty(result.Secondary);
        Assert.Equal(2, generator.Prompts.Count);
        Assert.Same(result, Assert.Single(repository.Persisted));
    }

    [Fact]
    public async Task TerminalUnknownSubjectIdDoesNotEchoModelControlledText()
    {
        const string sensitiveMarker = "PRIVATE-SYNTHETIC-DOCUMENT-CONTENT";
        string response = $$"""
                            {"primary":{"subjectId":"{{sensitiveMarker}}","confidence":0.9,"evidence":["pump"]},"secondary":[]}
                            """;
        var generator = new ScriptedSubjectGenerator(response, response);
        var repository = new InMemorySubjectAssignmentRepository();
        var classifier = new SubjectClassifier(generator, new FixedSubjectTimeProvider());

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            classifier.ClassifyAsync(repository,
                                     SubjectTestData.Descriptor(),
                                     SubjectTestData.Catalog(),
                                     "2026-08-04",
                                     "scan-private-id",
                                     TestContext.Current.CancellationToken));

        Assert.Equal(2, generator.Prompts.Count);
        Assert.DoesNotContain(sensitiveMarker, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveMarker, failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(repository.Persisted);
    }

    [Fact]
    public async Task LowConfidenceAssignmentNeedsReviewAndStillPersists()
    {
        var generator = new ScriptedSubjectGenerator(
            """
            {"primary":{"subjectId":"subject-hydraulics","confidence":0.40,"evidence":["pump"]},"secondary":[]}
            """);
        var repository = new InMemorySubjectAssignmentRepository();
        var classifier = new SubjectClassifier(generator, new FixedSubjectTimeProvider());

        SubjectAssignmentRecord result = await classifier.ClassifyAsync(repository,
                                                                         SubjectTestData.Descriptor(),
                                                                         SubjectTestData.Catalog(),
                                                                         "2026-08-04",
                                                                         "scan-1",
                                                                         TestContext.Current.CancellationToken);

        Assert.True(result.NeedsReview);
        Assert.Single(repository.Persisted);
    }

    [Fact]
    public async Task LowConfidenceSecondaryAlsoMarksPersistedAssignmentForReview()
    {
        var generator = new ScriptedSubjectGenerator(
            """
            {"primary":{"subjectId":"subject-hydraulics","confidence":0.90,"evidence":["pump"]},
             "secondary":[{"subjectId":"subject-safety","confidence":0.40,"evidence":["lockout"]}]}
            """);
        var repository = new InMemorySubjectAssignmentRepository();
        var classifier = new SubjectClassifier(generator, new FixedSubjectTimeProvider());

        SubjectAssignmentRecord result = await classifier.ClassifyAsync(repository,
                                                                         SubjectTestData.Descriptor(),
                                                                         SubjectTestData.Catalog(),
                                                                         "2026-08-04",
                                                                         "scan-1",
                                                                         TestContext.Current.CancellationToken);

        Assert.True(result.NeedsReview);
        Assert.Same(result, Assert.Single(repository.Persisted));
    }

    [Theory]
    [InlineData("{not-json}")]
    [InlineData("{\"primary\":{\"subjectId\":\"subject-unknown\",\"confidence\":0.9,\"evidence\":[]},\"secondary\":[]}")]
    [InlineData("{\"primary\":{\"subjectId\":\"id\",\"confidence\":0.9,\"evidence\":[\"pump\"]},\"secondary\":[]}")]
    [InlineData("{\"primary\":{\"subjectId\":\"existing-id-or-null\",\"confidence\":0.9,\"evidence\":[\"pump\"]},\"secondary\":[]}")]
    [InlineData("{\"primary\":{\"subjectId\":\"subject-hydraulics\",\"confidence\":1.2,\"evidence\":[]},\"secondary\":[]}")]
    [InlineData("{\"primary\":{\"subjectId\":\"subject-hydraulics\",\"confidence\":0.9,\"evidence\":[]},\"secondary\":[]}")]
    [InlineData("{\"primary\":{\"subjectId\":\"subject-hydraulics\",\"confidence\":0.9,\"evidence\":[\"pump\",\"pump\"]},\"secondary\":[]}")]
    [InlineData("{\"primary\":{\"subjectId\":\"subject-hydraulics\",\"confidence\":0.9,\"evidence\":[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\",\"7\"]},\"secondary\":[]}")]
    [InlineData("{\"primary\":{\"subjectId\":\"subject-hydraulics\",\"confidence\":0.9,\"evidence\":[\"pump\"]},\"secondary\":[null]}")]
    [InlineData("{\"primary\":{\"subjectId\":\"subject-hydraulics\",\"confidence\":0.9,\"evidence\":[]},\"secondary\":[{\"subjectId\":\"subject-safety\",\"confidence\":0.8,\"evidence\":[]},{\"subjectId\":\"subject-electrical\",\"confidence\":0.7,\"evidence\":[]},{\"subjectId\":\"subject-hydraulics\",\"confidence\":0.6,\"evidence\":[]},{\"subjectId\":\"subject-safety\",\"confidence\":0.5,\"evidence\":[]}]}")]
    public async Task InvalidStructuredResponseIsRejectedWithoutPersistence(string response)
    {
        var repository = new InMemorySubjectAssignmentRepository();
        string[] responses = [response, response];
        var classifier = new SubjectClassifier(new ScriptedSubjectGenerator(responses),
                                               new FixedSubjectTimeProvider());

        await Assert.ThrowsAsync<InvalidDataException>(() => classifier.ClassifyAsync(
                                                           repository,
                                                           SubjectTestData.Descriptor(),
                                                           SubjectTestData.Catalog(),
                                                           "2026-08-04",
                                                           "scan-1",
                                                           TestContext.Current.CancellationToken));

        Assert.Empty(repository.Persisted);
    }

    [Fact]
    public async Task OversizedEvidenceIsRejectedWithoutPersistence()
    {
        string evidence = new('x', SubjectClassificationLimits.MaxEvidenceCharacters + 1);
        string response = $$"""
                            {"primary":{"subjectId":"subject-hydraulics","confidence":0.9,"evidence":["{{evidence}}"]},"secondary":[]}
                            """;
        var repository = new InMemorySubjectAssignmentRepository();
        var classifier = new SubjectClassifier(new ScriptedSubjectGenerator(response, response),
                                               new FixedSubjectTimeProvider());

        await Assert.ThrowsAsync<InvalidDataException>(() => classifier.ClassifyAsync(
                                                           repository,
                                                           SubjectTestData.Descriptor(),
                                                           SubjectTestData.Catalog(),
                                                           "2026-08-04",
                                                           "scan-1",
                                                           TestContext.Current.CancellationToken));

        Assert.Empty(repository.Persisted);
    }
}
