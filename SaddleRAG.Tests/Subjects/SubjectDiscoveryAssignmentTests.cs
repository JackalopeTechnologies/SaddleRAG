// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Subjects;

namespace SaddleRAG.Tests.Subjects;

public sealed class SubjectDiscoveryAssignmentTests
{
    [Fact]
    public async Task SimilarManualsRetainTheirOwnDiscoveredSubjectsWithoutAnotherGeneration()
    {
        SubjectDescriptor communications = SubjectTestData.Descriptor("document-1", "revision-1") with
            { Title = "VF-S11 Communications Function Instruction Manual" };
        SubjectDescriptor instructions = SubjectTestData.Descriptor("document-2", "revision-2") with
            { Title = "VF-S11 Inverter Instruction Manual" };
        var generator = new ScriptedSubjectGenerator(Proposal(communications.Title), Proposal(instructions.Title));
        var catalogs = new InMemorySubjectCatalogRepository();
        var assignments = new InMemorySubjectAssignmentRepository();
        var builder = new SubjectCatalogBuilder(generator,
            new SequenceSubjectIdGenerator("subject-communications", "subject-instructions"), new FixedSubjectTimeProvider());

        SubjectCatalogReconciliation result = await builder.ReconcileDocumentsAsync(catalogs, LibraryId, ScanRunId,
            [instructions, communications], TestContext.Current.CancellationToken);
        SubjectAssignmentRecord second = await result.PersistAssignmentAsync(assignments, instructions, Version, ScanRunId,
            TestContext.Current.CancellationToken);
        SubjectAssignmentRecord first = await result.PersistAssignmentAsync(assignments, communications, Version, ScanRunId,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, generator.Prompts.Count);
        Assert.Equal("subject-communications", first.Primary.SubjectId);
        Assert.Equal("subject-instructions", second.Primary.SubjectId);
        Assert.Empty(first.Secondary);
        Assert.Empty(second.Secondary);
        Assert.Equal([communications.Title], first.Primary.Evidence);
        Assert.Equal([instructions.Title], second.Primary.Evidence);
        Assert.All(assignments.Persisted, assignment =>
        {
            Assert.Equal(SubjectCatalogPrompt.PromptVersion, assignment.Provenance.PromptVersion);
            Assert.Equal(result.Catalog.TaxonomyVersion, assignment.TaxonomyVersion);
            Assert.Equal(Version, assignment.Version);
            Assert.Equal(ScanRunId, assignment.ScanRunId);
        });
    }

    [Fact]
    public async Task GeneratedDescriptionCannotIntroduceAnUnsupportedModelNumber()
    {
        SubjectDescriptor descriptor = SubjectTestData.Descriptor() with { Title = "0034SDSR41A-P 3HP motor" };
        var generator = new ScriptedSubjectGenerator(
            """{"concepts":[{"label":"0034SDSR41A-P motor","aliases":[],"confidence":0.95,"description":"Invented model SDSDR41A-P","evidence":["source-1"]}]}""");
        var builder = new SubjectCatalogBuilder(generator,
            new SequenceSubjectIdGenerator("subject-motor"), new FixedSubjectTimeProvider());

        SubjectCatalogRecord result = await builder.ReconcileAsync(new InMemorySubjectCatalogRepository(), LibraryId,
            ScanRunId, [descriptor], TestContext.Current.CancellationToken);

        Assert.Equal(descriptor.Title, Assert.Single(result.Concepts).Description);
    }

    [Fact]
    public async Task PublishedIdentityAndCurrentDiscoveryConfidenceArePreserved()
    {
        var catalogs = new InMemorySubjectCatalogRepository();
        SubjectCatalogRecord existing = SubjectTestData.Catalog();
        catalogs.Seed(existing);
        var generator = new ScriptedSubjectGenerator(
            """{"concepts":[{"label":"hydraulic","confidence":0.95,"evidence":["source-1"]},{"label":"Safety","confidence":0.4,"evidence":["source-1"]}]}""");
        var builder = new SubjectCatalogBuilder(generator, new SequenceSubjectIdGenerator(), new FixedSubjectTimeProvider());
        SubjectDescriptor descriptor = SubjectTestData.Descriptor();
        var assignments = new InMemorySubjectAssignmentRepository();

        SubjectCatalogReconciliation result = await builder.ReconcileDocumentsAsync(catalogs, LibraryId, ScanRunId,
            [descriptor], TestContext.Current.CancellationToken);
        SubjectAssignmentRecord assignment = await result.PersistAssignmentAsync(assignments, descriptor, Version,
            ScanRunId, TestContext.Current.CancellationToken);

        Assert.Same(existing, result.Catalog);
        Assert.Equal("subject-hydraulics", assignment.Primary.SubjectId);
        Assert.Equal("subject-safety", Assert.Single(assignment.Secondary).SubjectId);
        Assert.Equal(0.4f, assignment.Secondary[0].Confidence);
        Assert.True(assignment.NeedsReview);
        Assert.Equal(SubjectCatalogPrompt.PromptVersion, assignment.Provenance.PromptVersion);
        Assert.Single(generator.Prompts);
    }

    [Fact]
    public async Task ARevisionFromAnotherDocumentCannotReceiveTheCapturedAssignment()
    {
        var generator = new ScriptedSubjectGenerator(Proposal("Hydraulics"));
        var builder = new SubjectCatalogBuilder(generator,
            new SequenceSubjectIdGenerator("subject-pump"), new FixedSubjectTimeProvider());
        SubjectDescriptor descriptor = SubjectTestData.Descriptor();
        var assignments = new InMemorySubjectAssignmentRepository();
        SubjectCatalogReconciliation result = await builder.ReconcileDocumentsAsync(new InMemorySubjectCatalogRepository(),
            LibraryId, ScanRunId, [descriptor], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => result.PersistAssignmentAsync(assignments,
            descriptor with { DocumentId = "another-document" }, Version, ScanRunId, TestContext.Current.CancellationToken));

        Assert.Empty(assignments.Persisted);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("-0.1")]
    [InlineData("1.1")]
    public async Task InvalidConfidenceCannotPublishACatalogOrAssignment(string confidence)
    {
        string response = $$"""{"concepts":[{"label":"Hydraulics","confidence":{{confidence}},"evidence":["source-1"]}]}""";
        var generator = new ScriptedSubjectGenerator(response, response);
        var catalogs = new InMemorySubjectCatalogRepository();
        var builder = new SubjectCatalogBuilder(generator, new SequenceSubjectIdGenerator(), new FixedSubjectTimeProvider());

        await Assert.ThrowsAsync<InvalidDataException>(() => builder.ReconcileDocumentsAsync(catalogs, LibraryId,
            ScanRunId, [SubjectTestData.Descriptor()], TestContext.Current.CancellationToken));

        Assert.Empty(catalogs.Inserted);
        Assert.Equal(2, generator.Prompts.Count);
    }

    private static string Proposal(string label) => SubjectJson.Serialize(new
        { Concepts = new[] { new { Label = label, Confidence = 0.95f, Evidence = new[] { "source-1" } } } });

    private const string LibraryId = "manual-library";
    private const string ScanRunId = "scan-single-pass";
    private const string Version = "2026-10-07";
}
