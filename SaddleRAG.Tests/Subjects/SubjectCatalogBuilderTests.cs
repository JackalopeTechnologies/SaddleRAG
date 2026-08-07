// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Subjects;

namespace SaddleRAG.Tests.Subjects;

public sealed class SubjectCatalogBuilderTests
{
    [Fact]
    public async Task SemanticNoOpReusesExistingTaxonomyWithoutInsert()
    {
        SubjectCatalogRecord existing = SubjectTestData.Catalog();
        var repository = new InMemorySubjectCatalogRepository();
        repository.Seed(existing);
        var generator = new ScriptedSubjectGenerator(
            """
            {"concepts":[
              {"subjectId":"subject-hydraulics","label":"Hydraulics","aliases":["hydraulic","fluid power"],"description":"Hydraulic power, pumps, and circuits."},
              {"subjectId":"subject-safety","label":"Safety","aliases":["lockout","LOTO"],"description":"Safe work and energy isolation."},
              {"subjectId":"subject-electrical","label":"Electrical controls","aliases":["controls"],"description":"Electrical control systems."}
            ]}
            """);
        var builder = new SubjectCatalogBuilder(generator,
                                                new SequenceSubjectIdGenerator(),
                                                new FixedSubjectTimeProvider());

        SubjectCatalogRecord result = await builder.ReconcileAsync(repository,
                                                                   "manual-library",
                                                                   "scan-no-op",
                                                                   [SubjectTestData.Descriptor()],
                                                                   TestContext.Current.CancellationToken);

        Assert.Same(existing, result);
        Assert.Single(repository.Inserted);
    }

    [Fact]
    public async Task LaterScanReusesOpaqueIdsAddsConceptAndPreservesPriorRevision()
    {
        const string hydraulicsId = "subject-00000000000000000000000000000001";
        const string safetyId = "subject-00000000000000000000000000000002";
        const string electricalId = "subject-00000000000000000000000000000003";
        var generator = new ScriptedSubjectGenerator(
            """
            {"concepts":[
              {"subjectId":null,"label":"Hydraulics","aliases":["fluid power"],"description":"Pumps and circuits"},
              {"subjectId":null,"label":"Safety","aliases":["lockout"],"description":"Safe work"}
            ]}
            """,
            $$"""
            {"concepts":[
              {"subjectId":"{{hydraulicsId}}","label":"Hydraulics","aliases":["fluid power"],"description":"Pumps and circuits"},
              {"subjectId":null,"label":"Electrical controls","aliases":["controls"],"description":"Electrical systems"}
            ]}
            """);
        var repository = new InMemorySubjectCatalogRepository();
        var builder = new SubjectCatalogBuilder(generator,
                                                new SequenceSubjectIdGenerator(hydraulicsId, safetyId, electricalId),
                                                new FixedSubjectTimeProvider());

        SubjectCatalogRecord first = await builder.ReconcileAsync(repository,
                                                                  "manual-library",
                                                                  "scan-first",
                                                                  [SubjectTestData.Descriptor()],
                                                                  TestContext.Current.CancellationToken);
        SubjectCatalogRecord second = await builder.ReconcileAsync(repository,
                                                                   "manual-library",
                                                                   "scan-second",
                                                                   [SubjectTestData.Descriptor("document-controls",
                                                                                               "revision-controls")],
                                                                   TestContext.Current.CancellationToken);

        Assert.Equal(1, first.Revision);
        Assert.Equal(2, second.Revision);
        Assert.NotEqual(first.TaxonomyVersion, second.TaxonomyVersion);
        Assert.Equal(first.TaxonomyVersion, second.PreviousTaxonomyVersion);
        Assert.Equal([hydraulicsId, safetyId], first.Concepts.Select(c => c.Id).ToArray());
        Assert.Contains(second.Concepts, c => c.Id == hydraulicsId && c.Label == "Hydraulics");
        Assert.Contains(second.Concepts, c => c.Id == safetyId && c.Label == "Safety");
        Assert.Contains(second.Concepts, c => c.Id == electricalId && c.Label == "Electrical controls");
        Assert.Equal(2, first.Concepts.Count);
        Assert.Equal(2, repository.Inserted.Count);
        Assert.Equal("scan-first", first.ScanRunId);
        Assert.Equal("scan-second", second.ScanRunId);
        Assert.All(repository.Inserted,
                   catalog =>
                   {
                       Assert.Equal("scripted", catalog.Provenance.Backend);
                       Assert.Equal("scripted-subject-model", catalog.Provenance.ModelId);
                       Assert.Equal(SubjectCatalogPrompt.PromptVersion, catalog.Provenance.PromptVersion);
                       Assert.Equal(SubjectTestData.GeneratedAtUtc, catalog.Provenance.GeneratedAtUtc);
                   });
    }
}
