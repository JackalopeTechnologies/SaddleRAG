// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>A catalog and the document selections captured during its discovery pass.</summary>
public sealed class SubjectCatalogReconciliation
{
    internal SubjectCatalogReconciliation(SubjectCatalogRecord catalog,
        IReadOnlyDictionary<string, (string DocumentId, IReadOnlyList<SubjectSelection> Selections)> documents,
        SubjectClassifierProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(provenance);
        Catalog = catalog;
        mDocuments = documents;
        mProvenance = provenance;
    }

    private readonly IReadOnlyDictionary<string, (string DocumentId, IReadOnlyList<SubjectSelection> Selections)> mDocuments;
    private readonly SubjectClassifierProvenance mProvenance;

    /// <summary>The immutable catalog revision containing the selected stable identities.</summary>
    public SubjectCatalogRecord Catalog { get; }

    /// <summary>Saves a captured document classification without calling the model again.</summary>
    public async Task<SubjectAssignmentRecord> PersistAssignmentAsync(ISubjectAssignmentRepository repository,
        SubjectDescriptor descriptor, string version, string scanRunId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);
        ct.ThrowIfCancellationRequested();
        if (!mDocuments.TryGetValue(descriptor.DocumentRevisionId, out var document) ||
            !document.DocumentId.Equals(descriptor.DocumentId, StringComparison.Ordinal))
            throw new ArgumentException("The document revision was not classified in this discovery pass.", nameof(descriptor));

        var result = new SubjectAssignmentRecord
                         {
                             Id = SubjectAssignmentRepository.MakeId(Catalog.LibraryId, version, descriptor.DocumentRevisionId),
                             LibraryId = Catalog.LibraryId,
                             Version = version,
                             ScanRunId = scanRunId,
                             DocumentId = descriptor.DocumentId,
                             DocumentRevisionId = descriptor.DocumentRevisionId,
                             TaxonomyVersion = Catalog.TaxonomyVersion,
                             Primary = document.Selections[0],
                             Secondary = document.Selections.Skip(1).ToArray(),
                             NeedsReview = document.Selections.Any(selection => selection.Confidence <
                                 SubjectClassificationLimits.NeedsReviewConfidenceThreshold),
                             Provenance = mProvenance
                         };
        await repository.PersistAsync(result, ct);
        return result;
    }
}
