// SubjectSearchEnricher.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Bulk joins subject assignments and catalog labels onto ranked chunks.</summary>
public sealed class SubjectSearchEnricher
{
    public SubjectSearchEnricher(ISubjectAssignmentRepository assignments,
                                 ISubjectCatalogRepository catalogs)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(catalogs);
        mAssignments = assignments;
        mCatalogs = catalogs;
    }

    private readonly ISubjectAssignmentRepository mAssignments;
    private readonly ISubjectCatalogRepository mCatalogs;

    public async Task<IReadOnlyDictionary<string, SubjectSearchMetadata>> EnrichAsync(
        IReadOnlyCollection<DocChunk> chunks,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var revisionIds = chunks
                         .Select(chunk => chunk.DocumentSource?.RevisionId)
                         .Where(revisionId => !string.IsNullOrEmpty(revisionId))
                         .Cast<string>()
                         .Distinct(StringComparer.Ordinal)
                         .ToList();
        var result = new Dictionary<string, SubjectSearchMetadata>(StringComparer.Ordinal);
        if (revisionIds.Count > 0)
        {
            IReadOnlyList<SubjectAssignmentRecord> assignmentRows =
                await mAssignments.GetByDocumentRevisionIdsAsync(revisionIds, ct);
            var assignmentsByRevision = assignmentRows
                                       .GroupBy(assignment => assignment.DocumentRevisionId, StringComparer.Ordinal)
                                       .ToDictionary(group => group.Key,
                                                     group => group.OrderByDescending(assignment => assignment.Version,
                                                                                      StringComparer.Ordinal).First(),
                                                     StringComparer.Ordinal);
            var catalogKeys = assignmentRows
                             .Select(assignment => new SubjectCatalogKey(assignment.LibraryId,
                                                                        assignment.TaxonomyVersion))
                             .Distinct()
                             .ToList();
            IReadOnlyList<SubjectCatalogRecord> catalogRows = await mCatalogs.GetManyAsync(catalogKeys, ct);
            var catalogsByKey = catalogRows.ToDictionary(catalog => new SubjectCatalogKey(catalog.LibraryId,
                                                                                           catalog.TaxonomyVersion));

            foreach(DocChunk chunk in chunks)
            {
                SubjectSearchMetadata? metadata = BuildMetadata(chunk,
                                                                assignmentsByRevision,
                                                                catalogsByKey);
                if (metadata != null)
                    result[chunk.Id] = metadata;
            }
        }

        return result;
    }

    private static SubjectSearchMetadata? BuildMetadata(
        DocChunk chunk,
        IReadOnlyDictionary<string, SubjectAssignmentRecord> assignmentsByRevision,
        IReadOnlyDictionary<SubjectCatalogKey, SubjectCatalogRecord> catalogsByKey)
    {
        SubjectSearchMetadata? result = null;
        string? revisionId = chunk.DocumentSource?.RevisionId;
        if (revisionId != null &&
            assignmentsByRevision.TryGetValue(revisionId, out SubjectAssignmentRecord? assignment) &&
            (chunk.SubjectTaxonomyVersion == null ||
             string.Equals(chunk.SubjectTaxonomyVersion,
                           assignment.TaxonomyVersion,
                           StringComparison.Ordinal)) &&
            catalogsByKey.TryGetValue(new SubjectCatalogKey(assignment.LibraryId,
                                                             assignment.TaxonomyVersion),
                                       out SubjectCatalogRecord? catalog))
        {
            var conceptsById = catalog.Concepts.ToDictionary(concept => concept.Id, StringComparer.Ordinal);
            var subjects = new List<SubjectSearchPresentation>();
            AddPresentation(subjects, assignment.Primary, PrimaryRole, conceptsById);
            foreach(SubjectSelection secondary in assignment.Secondary)
                AddPresentation(subjects, secondary, SecondaryRole, conceptsById);

            result = new SubjectSearchMetadata
                         {
                             ChunkId = chunk.Id,
                             TaxonomyVersion = assignment.TaxonomyVersion,
                             NeedsReview = assignment.NeedsReview,
                             Subjects = subjects
                         };
        }

        return result;
    }

    private static void AddPresentation(ICollection<SubjectSearchPresentation> target,
                                        SubjectSelection selection,
                                        string role,
                                        IReadOnlyDictionary<string, SubjectConcept> conceptsById)
    {
        if (conceptsById.TryGetValue(selection.SubjectId, out SubjectConcept? concept))
        {
            target.Add(new SubjectSearchPresentation
                           {
                               Id = selection.SubjectId,
                               Label = concept.Label,
                               Role = role,
                               Confidence = selection.Confidence,
                               Evidence = selection.Evidence
                           });
        }
    }

    private const string PrimaryRole = "primary";
    private const string SecondaryRole = "secondary";
}
