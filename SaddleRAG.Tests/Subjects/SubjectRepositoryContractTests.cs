// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Subjects;

namespace SaddleRAG.Tests.Subjects;

public sealed class SubjectRepositoryContractTests
{
    [Fact]
    public void RepositoryIdsAreDeterministicAndScopeImmutableRecords()
    {
        Assert.Equal("manual-library/taxonomy-000001",
                     SubjectCatalogRepository.MakeId("manual-library", "taxonomy-000001"));
        Assert.Equal("manual-library/2026-08-04/revision-1/subjects",
                     SubjectAssignmentRepository.MakeId("manual-library", "2026-08-04", "revision-1"));
    }

    [Fact]
    public void CatalogAndAssignmentBsonPreserveVersionedProvenance()
    {
        SubjectCatalogRecord catalog = SubjectTestData.Catalog();
        var assignment = new SubjectAssignmentRecord
                             {
                                 Id = SubjectAssignmentRepository.MakeId("manual-library",
                                                                         "2026-08-04",
                                                                         "revision-hydraulics"),
                                 LibraryId = "manual-library",
                                 Version = "2026-08-04",
                                 ScanRunId = "scan-1",
                                 DocumentId = "document-hydraulics",
                                 DocumentRevisionId = "revision-hydraulics",
                                 TaxonomyVersion = catalog.TaxonomyVersion,
                                 Primary = new SubjectSelection
                                     {
                                         SubjectId = "subject-hydraulics",
                                         Confidence = 0.9f,
                                         Evidence = ["pump"]
                                     },
                                 NeedsReview = false,
                                 Provenance = SubjectTestData.Provenance(SubjectClassificationPrompt.PromptVersion)
                             };

        SubjectCatalogRecord catalogRoundTrip = BsonSerializer.Deserialize<SubjectCatalogRecord>(catalog.ToBson());
        SubjectAssignmentRecord assignmentRoundTrip = BsonSerializer.Deserialize<SubjectAssignmentRecord>(
            assignment.ToBson());

        Assert.Equal(catalog.TaxonomyVersion, catalogRoundTrip.TaxonomyVersion);
        Assert.Equal(catalog.Provenance, catalogRoundTrip.Provenance);
        Assert.Equal(assignment.TaxonomyVersion, assignmentRoundTrip.TaxonomyVersion);
        Assert.Equal(assignment.Provenance, assignmentRoundTrip.Provenance);
    }
}
