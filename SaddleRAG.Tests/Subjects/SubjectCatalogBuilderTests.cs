// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Subjects;

namespace SaddleRAG.Tests.Subjects;

public sealed class SubjectCatalogBuilderTests
{
    [Theory]
    [InlineData("Hydraulics")]
    [InlineData("hydraulic")]
    public async Task KnownLabelOrAliasReusesIdentityAndPreservesPublishedMeaning(string label)
    {
        SubjectCatalogRecord existing = SubjectTestData.Catalog();
        var repository = new InMemorySubjectCatalogRepository();
        repository.Seed(existing);
        var generator = new ScriptedSubjectGenerator(Proposal(label, "New description must not overwrite old meaning."));
        var builder = new SubjectCatalogBuilder(generator, new SequenceSubjectIdGenerator(), new FixedSubjectTimeProvider());

        SubjectCatalogRecord result = await builder.ReconcileAsync(repository, LibraryId, "scan-no-op",
            [SubjectTestData.Descriptor()], TestContext.Current.CancellationToken);

        Assert.Same(existing, result);
        Assert.Single(repository.Inserted);
        Assert.DoesNotContain("subject-hydraulics", Assert.Single(generator.Prompts), StringComparison.Ordinal);
        Assert.DoesNotContain("Electrical control systems.", generator.Prompts[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaterScanAddsConceptAndPreservesPriorRevisionAndProvenance()
    {
        var repository = new InMemorySubjectCatalogRepository();
        var generator = new ScriptedSubjectGenerator(Proposal("Hydraulics"), Proposal("Safety"));
        var builder = new SubjectCatalogBuilder(generator,
            new SequenceSubjectIdGenerator("subject-first", "subject-second"), new FixedSubjectTimeProvider());

        SubjectCatalogRecord first = await builder.ReconcileAsync(repository, LibraryId, "scan-first",
            [SubjectTestData.Descriptor()], TestContext.Current.CancellationToken);
        Assert.Equal(SubjectCatalogPublicationState.Candidate, first.PublicationState);
        Assert.True(await repository.TryPublishCandidateAsync(LibraryId, first.TaxonomyVersion, "scan-first",
            TestContext.Current.CancellationToken));
        SubjectCatalogRecord second = await builder.ReconcileAsync(repository, LibraryId, "scan-second",
            [SubjectTestData.Descriptor()], TestContext.Current.CancellationToken);

        Assert.Equal(1, first.Revision);
        Assert.Equal(2, second.Revision);
        Assert.Equal(first.TaxonomyVersion, second.PreviousTaxonomyVersion);
        Assert.Equal("subject-first", Assert.Single(first.Concepts).Id);
        Assert.Equal(["subject-first", "subject-second"], second.Concepts.Select(concept => concept.Id));
        Assert.Equal(2, repository.Inserted.Count);
        Assert.All(repository.Inserted, catalog =>
        {
            Assert.Equal("scripted", catalog.Provenance.Backend);
            Assert.Equal("scripted-subject-model", catalog.Provenance.ModelId);
            Assert.Equal(SubjectCatalogPrompt.PromptVersion, catalog.Provenance.PromptVersion);
            Assert.Equal(SubjectTestData.GeneratedAtUtc, catalog.Provenance.GeneratedAtUtc);
        });
    }

    [Theory]
    [InlineData("subject-first")]
    [InlineData("invented-id")]
    public async Task ModelSuppliedIdentityCannotRenameAnotherManual(string returnedId)
    {
        var generator = new ScriptedSubjectGenerator(
            """{"concepts":[{"label":"VF-S9 Communications","aliases":["inverter"],"confidence":0.95,"evidence":["VF-S9"]}]}""",
            $$"""{"concepts":[{"subjectId":"{{returnedId}}","label":"VF-AS3 Instructions","aliases":["inverter"],"confidence":0.95,"evidence":["VF-AS3"]}]}""");
        var repository = new InMemorySubjectCatalogRepository();
        var builder = new SubjectCatalogBuilder(generator,
            new SequenceSubjectIdGenerator("subject-first", "subject-second"), new FixedSubjectTimeProvider());
        SubjectDescriptor first = SubjectTestData.Descriptor("document-1", "revision-1") with { Title = "VF-S9 Communications" };
        SubjectDescriptor second = SubjectTestData.Descriptor("document-2", "revision-2") with { Title = "VF-AS3 Instructions" };

        SubjectCatalogRecord result = await builder.ReconcileAsync(repository, LibraryId, "scan-models",
            [first, second], TestContext.Current.CancellationToken);

        Assert.Collection(result.Concepts,
            concept => { Assert.Equal("subject-first", concept.Id); Assert.Equal("VF-S9 Communications", concept.Label); },
            concept => { Assert.Equal("subject-second", concept.Id); Assert.Equal("VF-AS3 Instructions", concept.Label); });
        Assert.DoesNotContain("VF-S9", generator.Prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncomingAliasCannotReplaceAnExistingSubject()
    {
        var repository = new InMemorySubjectCatalogRepository();
        repository.Seed(SubjectTestData.Catalog());
        var generator = new ScriptedSubjectGenerator(
            """{"concepts":[{"label":"Pump maintenance","aliases":["Safety"],"confidence":0.95,"evidence":["Pump service"]}]}""");
        var builder = new SubjectCatalogBuilder(generator,
            new SequenceSubjectIdGenerator("subject-pump"), new FixedSubjectTimeProvider());

        SubjectCatalogRecord result = await builder.ReconcileAsync(repository, LibraryId, "scan-alias",
            [SubjectTestData.Descriptor()], TestContext.Current.CancellationToken);

        Assert.Equal(4, result.Concepts.Count);
        Assert.Contains(result.Concepts, concept => concept.Id == "subject-safety" && concept.Label == "Safety");
        Assert.Contains(result.Concepts, concept => concept.Id == "subject-pump" && concept.Label == "Pump maintenance");
    }

    [Fact]
    public async Task ExactLabelWinsOverAnAliasOnAnotherConcept()
    {
        SubjectCatalogRecord existing = SubjectTestData.Catalog();
        var repository = new InMemorySubjectCatalogRepository();
        repository.Seed(existing with
        {
            Concepts = [existing.Concepts[0], existing.Concepts[1] with { Aliases = ["Hydraulics"] }]
        });
        var generator = new ScriptedSubjectGenerator(Proposal("Hydraulics"));
        var builder = new SubjectCatalogBuilder(generator, new SequenceSubjectIdGenerator(), new FixedSubjectTimeProvider());

        SubjectCatalogRecord result = await builder.ReconcileAsync(repository, LibraryId, "scan-exact",
            [SubjectTestData.Descriptor()], TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Concepts.Count);
        Assert.Single(repository.Inserted);
    }

    [Fact]
    public async Task AmbiguousPublishedAliasCannotMergeSubjects()
    {
        SubjectCatalogRecord existing = SubjectTestData.Catalog();
        var repository = new InMemorySubjectCatalogRepository();
        repository.Seed(existing with
        {
            Concepts = existing.Concepts.Select(concept => concept with { Aliases = ["Shared alias"] }).ToArray()
        });
        var generator = new ScriptedSubjectGenerator(Proposal("Shared alias"));
        var builder = new SubjectCatalogBuilder(generator, new SequenceSubjectIdGenerator(), new FixedSubjectTimeProvider());

        await Assert.ThrowsAsync<InvalidDataException>(() => builder.ReconcileAsync(repository, LibraryId, "scan-ambiguous",
            [SubjectTestData.Descriptor()], TestContext.Current.CancellationToken));

        Assert.Single(repository.Inserted);
    }

    [Theory]
    [InlineData("{\"concepts\":[null]}")]
    [InlineData("{\"concepts\":[]}")]
    [InlineData("{\"concepts\":[{\"label\":\"Inverter\",\"confidence\":0.95,\"evidence\":[\"VF-S15 instruction manual\"]}]}")]
    [InlineData("{\"concepts\":[{\"label\":\"Pump\",\"confidence\":0.95}]}")]
    public async Task InvalidOrInventedEvidenceIsRetriedAndNeverPublished(string response)
    {
        var repository = new InMemorySubjectCatalogRepository();
        var generator = new ScriptedSubjectGenerator(response, response);
        var builder = new SubjectCatalogBuilder(generator, new SequenceSubjectIdGenerator(), new FixedSubjectTimeProvider());

        await Assert.ThrowsAsync<InvalidDataException>(() => builder.ReconcileAsync(repository, LibraryId, "scan-invalid",
            [SubjectTestData.Descriptor()], TestContext.Current.CancellationToken));

        Assert.Equal(2, generator.Prompts.Count);
        Assert.Empty(repository.Inserted);
    }

    [Fact]
    public async Task InvalidLaterProposalDoesNotLeakAnEarlierProposalIntoTheRetry()
    {
        var repository = new InMemorySubjectCatalogRepository();
        var generator = new ScriptedSubjectGenerator(
            """{"concepts":[{"label":"Safety","confidence":0.95,"evidence":["safety"]},null]}""",
            Proposal("Hydraulics"));
        var builder = new SubjectCatalogBuilder(generator,
            new SequenceSubjectIdGenerator("subject-only"), new FixedSubjectTimeProvider());

        SubjectCatalogRecord result = await builder.ReconcileAsync(repository, LibraryId, "scan-retry",
            [SubjectTestData.Descriptor()], TestContext.Current.CancellationToken);

        SubjectConcept concept = Assert.Single(result.Concepts);
        Assert.Equal("Hydraulics", concept.Label);
        Assert.Equal("subject-only", concept.Id);
        Assert.Equal(2, generator.Prompts.Count);
    }

    private static string Proposal(string label, string description = "Hydraulic pump service.") =>
        SubjectJson.Serialize(new
        {
            Concepts = new[] { new { Label = label, Aliases = Array.Empty<string>(), Description = description, Confidence = 0.95f, Evidence = new[] { "pump" } } }
        });

    private const string LibraryId = "manual-library";
}
