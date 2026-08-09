// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using MongoDB.Driver;

namespace SaddleRAG.Database.Repositories;

/// <summary>MongoDB persistence for immutable library subject catalogs.</summary>
public sealed class SubjectCatalogRepository : ISubjectCatalogRepository
{
    public SubjectCatalogRepository(SaddleRagDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        mContext = context;
    }

    private readonly SaddleRagDbContext mContext;

    public async Task<SubjectCatalogRecord?> GetLatestAsync(string libraryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        FilterDefinition<SubjectCatalogRecord> filter = Builders<SubjectCatalogRecord>.Filter.And(
            Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.LibraryId, libraryId),
            PublishedOrLegacyFilter());
        SubjectCatalogRecord? result = await mContext.SubjectCatalogs
                                                     .Find(filter)
                                                     .SortByDescending(catalog => catalog.Revision)
                                                     .FirstOrDefaultAsync(ct);
        return result;
    }

    public async Task<SubjectCatalogRecord?> GetAsync(string libraryId,
                                                      string taxonomyVersion,
                                                      CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(taxonomyVersion);
        SubjectCatalogRecord? result = await mContext.SubjectCatalogs
                                                     .Find(catalog => catalog.LibraryId == libraryId &&
                                                                      catalog.TaxonomyVersion == taxonomyVersion)
                                                     .FirstOrDefaultAsync(ct);
        return result;
    }

    public Task<IReadOnlyList<SubjectCatalogRecord>> GetManyAsync(IReadOnlyCollection<SubjectCatalogKey> keys,
                                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Task<IReadOnlyList<SubjectCatalogRecord>> result;
        if (keys.Count == 0)
            result = Task.FromResult<IReadOnlyList<SubjectCatalogRecord>>([]);
        else
        {
            var filters = keys.Select(key => Builders<SubjectCatalogRecord>.Filter.And(
                                          Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.LibraryId,
                                                                                   key.LibraryId),
                                          Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.TaxonomyVersion,
                                                                                   key.TaxonomyVersion)));
            result = GetManyCoreAsync(Builders<SubjectCatalogRecord>.Filter.Or(filters), ct);
        }

        return result;
    }

    public Task InsertRevisionAsync(SubjectCatalogRecord catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return mContext.SubjectCatalogs.InsertOneAsync(catalog, cancellationToken: ct);
    }

    public async Task<bool> TryPublishImportCandidateAsync(string libraryId,
                                                            string taxonomyVersion,
                                                            string importOperationId,
                                                            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(taxonomyVersion);
        ArgumentException.ThrowIfNullOrEmpty(importOperationId);
        FilterDefinition<SubjectCatalogRecord> candidateFilter = ImportCatalogFilter(
            libraryId,
            taxonomyVersion,
            importOperationId,
            SubjectCatalogPublicationState.Candidate);
        UpdateDefinition<SubjectCatalogRecord> publish =
            Builders<SubjectCatalogRecord>.Update.Set(catalog => catalog.PublicationState,
                                                       SubjectCatalogPublicationState.Published);
        UpdateResult updated = await mContext.SubjectCatalogs.UpdateOneAsync(candidateFilter,
                                                                              publish,
                                                                              cancellationToken: ct);
        bool result = updated.MatchedCount == 1;
        if (!result)
        {
            FilterDefinition<SubjectCatalogRecord> publishedFilter = ImportCatalogFilter(
                libraryId,
                taxonomyVersion,
                importOperationId,
                SubjectCatalogPublicationState.Published);
            result = await mContext.SubjectCatalogs.Find(publishedFilter).Limit(1).AnyAsync(ct);
        }

        return result;
    }

    public async Task<bool> TryRollbackImportCandidatePublicationAsync(
        string libraryId,
        string taxonomyVersion,
        string importOperationId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(taxonomyVersion);
        ArgumentException.ThrowIfNullOrEmpty(importOperationId);
        FilterDefinition<SubjectCatalogRecord> publishedFilter = ImportCatalogFilter(
            libraryId,
            taxonomyVersion,
            importOperationId,
            SubjectCatalogPublicationState.Published);
        UpdateDefinition<SubjectCatalogRecord> restoreCandidate =
            Builders<SubjectCatalogRecord>.Update.Set(catalog => catalog.PublicationState,
                                                       SubjectCatalogPublicationState.Candidate);
        UpdateResult updated = await mContext.SubjectCatalogs.UpdateOneAsync(publishedFilter,
                                                                              restoreCandidate,
                                                                              cancellationToken: ct);
        bool result = updated.MatchedCount == 1;
        if (!result)
        {
            FilterDefinition<SubjectCatalogRecord> candidateFilter = ImportCatalogFilter(
                libraryId,
                taxonomyVersion,
                importOperationId,
                SubjectCatalogPublicationState.Candidate);
            result = await mContext.SubjectCatalogs.Find(candidateFilter).Limit(1).AnyAsync(ct);
        }

        return result;
    }

    public async Task<ImportCatalogRollbackOutcome> TryRollbackImportCandidatePublicationIfUnreferencedAsync(
        string libraryId,
        string taxonomyVersion,
        string importOperationId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(taxonomyVersion);
        ArgumentException.ThrowIfNullOrEmpty(importOperationId);

        SubjectCatalogRecord? current = await GetAsync(libraryId, taxonomyVersion, ct);
        ImportCatalogRollbackOutcome result;
        if (current == null ||
            !string.Equals(current.ImportOperationId, importOperationId, StringComparison.Ordinal))
        {
            result = ImportCatalogRollbackOutcome.NotOwned;
        }
        else
        {
            result = current.PublicationState switch
            {
                SubjectCatalogPublicationState.Candidate => ImportCatalogRollbackOutcome.AlreadyCandidate,
                _ => await RollbackPublishedImportCatalogIfUnreferencedAsync(libraryId,
                                                                              taxonomyVersion,
                                                                              importOperationId,
                                                                              ct)
            };
        }

        return result;
    }

    private async Task<ImportCatalogRollbackOutcome> RollbackPublishedImportCatalogIfUnreferencedAsync(
        string libraryId,
        string taxonomyVersion,
        string importOperationId,
        CancellationToken ct)
    {
        bool referenceExists = await ImportCatalogReferenceExistsAsync(libraryId, taxonomyVersion, ct);
        ImportCatalogRollbackOutcome result;
        if (referenceExists)
        {
            result = ImportCatalogRollbackOutcome.ReferencedBySurvivor;
        }
        else
        {
            result = await TryDemotePublishedImportCatalogAsync(libraryId,
                                                                 taxonomyVersion,
                                                                 importOperationId,
                                                                 ct);
        }

        return result;
    }

    private async Task<bool> ImportCatalogReferenceExistsAsync(string libraryId,
                                                                string taxonomyVersion,
                                                                CancellationToken ct)
    {
        FilterDefinition<LibraryVersionRecord> versionFilter = Builders<LibraryVersionRecord>.Filter.And(
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.LibraryId, libraryId),
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.SubjectTaxonomyVersion, taxonomyVersion));
        bool versionReferenceExists = await mContext.LibraryVersions.Find(versionFilter)
                                                     .Limit(1)
                                                     .AnyAsync(ct);
        FilterDefinition<SubjectAssignmentRecord> assignmentFilter =
            Builders<SubjectAssignmentRecord>.Filter.And(
                Builders<SubjectAssignmentRecord>.Filter.Eq(assignment => assignment.LibraryId, libraryId),
                Builders<SubjectAssignmentRecord>.Filter.Eq(assignment => assignment.TaxonomyVersion,
                                                              taxonomyVersion));
        bool assignmentReferenceExists = await mContext.SubjectAssignments.Find(assignmentFilter)
                                                        .Limit(1)
                                                        .AnyAsync(ct);
        bool result = versionReferenceExists || assignmentReferenceExists;
        return result;
    }

    private async Task<ImportCatalogRollbackOutcome> TryDemotePublishedImportCatalogAsync(
        string libraryId,
        string taxonomyVersion,
        string importOperationId,
        CancellationToken ct)
    {
        FilterDefinition<SubjectCatalogRecord> publishedFilter = ImportCatalogFilter(
            libraryId,
            taxonomyVersion,
            importOperationId,
            SubjectCatalogPublicationState.Published);
        UpdateDefinition<SubjectCatalogRecord> restoreCandidate =
            Builders<SubjectCatalogRecord>.Update.Set(catalog => catalog.PublicationState,
                                                       SubjectCatalogPublicationState.Candidate);
        UpdateResult updated = await mContext.SubjectCatalogs.UpdateOneAsync(publishedFilter,
                                                                              restoreCandidate,
                                                                              cancellationToken: ct);
        ImportCatalogRollbackOutcome result;
        if (updated.MatchedCount == 1)
        {
            result = ImportCatalogRollbackOutcome.RolledBack;
        }
        else
        {
            SubjectCatalogRecord? current = await GetAsync(libraryId, taxonomyVersion, ct);
            result = current != null &&
                     current.PublicationState == SubjectCatalogPublicationState.Candidate &&
                     string.Equals(current.ImportOperationId, importOperationId, StringComparison.Ordinal)
                         ? ImportCatalogRollbackOutcome.AlreadyCandidate
                         : ImportCatalogRollbackOutcome.NotOwned;
        }

        return result;
    }

    public async Task<bool> DeleteImportCandidateIfUnreferencedAsync(
        string libraryId,
        string taxonomyVersion,
        string importOperationId,
        string deletingVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(taxonomyVersion);
        ArgumentException.ThrowIfNullOrEmpty(importOperationId);
        ArgumentException.ThrowIfNullOrEmpty(deletingVersion);
        var versionFilter = Builders<LibraryVersionRecord>.Filter.And(
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.LibraryId, libraryId),
            Builders<LibraryVersionRecord>.Filter.Ne(version => version.Version, deletingVersion),
            Builders<LibraryVersionRecord>.Filter.Or(
                Builders<LibraryVersionRecord>.Filter.Eq(version => version.SubjectTaxonomyVersion,
                                                           taxonomyVersion),
                Builders<LibraryVersionRecord>.Filter.Eq(version => version.PublicationState,
                                                           VersionPublicationState.Building)));
        bool versionReferenceExists = await mContext.LibraryVersions.Find(versionFilter)
                                                    .Limit(1)
                                                    .AnyAsync(ct);
        var assignmentFilter = Builders<SubjectAssignmentRecord>.Filter.And(
            Builders<SubjectAssignmentRecord>.Filter.Eq(assignment => assignment.LibraryId, libraryId),
            Builders<SubjectAssignmentRecord>.Filter.Eq(assignment => assignment.TaxonomyVersion,
                                                          taxonomyVersion));
        bool assignmentReferenceExists = await mContext.SubjectAssignments.Find(assignmentFilter)
                                                       .Limit(1)
                                                       .AnyAsync(ct);
        bool result = false;
        if (!versionReferenceExists && !assignmentReferenceExists)
        {
            FilterDefinition<SubjectCatalogRecord> candidateFilter = ImportCatalogFilter(
                libraryId,
                taxonomyVersion,
                importOperationId,
                SubjectCatalogPublicationState.Candidate);
            DeleteResult deletion = await mContext.SubjectCatalogs.DeleteOneAsync(candidateFilter, ct);
            result = deletion.DeletedCount == 1;
        }

        return result;
    }

    public async Task<bool> TryPublishCandidateAsync(string libraryId,
                                                      string taxonomyVersion,
                                                      string scanRunId,
                                                      CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(taxonomyVersion);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);
        FilterDefinition<SubjectCatalogRecord> candidateFilter =
            Builders<SubjectCatalogRecord>.Filter.And(
                Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.LibraryId, libraryId),
                Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.TaxonomyVersion,
                                                           taxonomyVersion),
                Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.ScanRunId, scanRunId),
                Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.PublicationState,
                                                           SubjectCatalogPublicationState.Candidate));
        UpdateDefinition<SubjectCatalogRecord> publish =
            Builders<SubjectCatalogRecord>.Update.Set(catalog => catalog.PublicationState,
                                                       SubjectCatalogPublicationState.Published);
        UpdateResult updated = await mContext.SubjectCatalogs.UpdateOneAsync(candidateFilter,
                                                                              publish,
                                                                              cancellationToken: ct);
        bool result = updated.MatchedCount == 1;
        if (!result)
        {
            FilterDefinition<SubjectCatalogRecord> publishedFilter =
                Builders<SubjectCatalogRecord>.Filter.And(
                    Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.LibraryId, libraryId),
                    Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.TaxonomyVersion,
                                                               taxonomyVersion),
                    PublishedOrLegacyFilter());
            result = await mContext.SubjectCatalogs.Find(publishedFilter).Limit(1).AnyAsync(ct);
        }

        return result;
    }

    public async Task<bool> TryRollbackCandidatePublicationAsync(string libraryId,
                                                                  string taxonomyVersion,
                                                                  string scanRunId,
                                                                  CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(taxonomyVersion);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);
        FilterDefinition<SubjectCatalogRecord> publishedCandidate =
            Builders<SubjectCatalogRecord>.Filter.And(
                Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.LibraryId, libraryId),
                Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.TaxonomyVersion,
                                                           taxonomyVersion),
                Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.ScanRunId, scanRunId),
                Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.PublicationState,
                                                           SubjectCatalogPublicationState.Published));
        UpdateDefinition<SubjectCatalogRecord> restoreCandidate =
            Builders<SubjectCatalogRecord>.Update.Set(catalog => catalog.PublicationState,
                                                       SubjectCatalogPublicationState.Candidate);
        UpdateResult updated = await mContext.SubjectCatalogs.UpdateOneAsync(publishedCandidate,
                                                                              restoreCandidate,
                                                                              cancellationToken: ct);
        return updated.MatchedCount == 1;
    }

    public async Task<long> DeleteCandidateScanRunAsync(string libraryId,
                                                        string scanRunId,
                                                        string? deletingVersion,
                                                        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);
        IReadOnlyList<LibraryVersionRecord> versions = await mContext.LibraryVersions
                                                                      .Find(version =>
                                                                          version.LibraryId == libraryId)
                                                                      .ToListAsync(ct);
        IReadOnlyList<SubjectAssignmentRecord> assignments = await mContext.SubjectAssignments
                                                                           .Find(assignment =>
                                                                               assignment.LibraryId == libraryId)
                                                                           .ToListAsync(ct);
        bool concurrentBuildExists = versions.Any(version =>
                                                       version.PublicationState ==
                                                       VersionPublicationState.Building &&
                                                       !string.Equals(version.Version,
                                                                      deletingVersion,
                                                                      StringComparison.Ordinal));
        long result = 0;
        if (!concurrentBuildExists)
        {
            IReadOnlyList<string> protectedTaxonomies = versions
                                                        .Where(version =>
                                                                   !string.Equals(version.Version,
                                                                                  deletingVersion,
                                                                                  StringComparison.Ordinal) &&
                                                                   !string.IsNullOrWhiteSpace(
                                                                       version.SubjectTaxonomyVersion))
                                                        .Select(version => version.SubjectTaxonomyVersion)
                                                        .OfType<string>()
                                                        .Concat(assignments.Select(assignment =>
                                                                                       assignment.TaxonomyVersion))
                                                        .Distinct(StringComparer.Ordinal)
                                                        .ToList();
            FilterDefinition<SubjectCatalogRecord> filter = Builders<SubjectCatalogRecord>.Filter.And(
                Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.LibraryId, libraryId),
                Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.ScanRunId, scanRunId),
                Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.PublicationState,
                                                           SubjectCatalogPublicationState.Candidate),
                Builders<SubjectCatalogRecord>.Filter.Nin(catalog => catalog.TaxonomyVersion,
                                                           protectedTaxonomies));
            DeleteResult deletion = await mContext.SubjectCatalogs.DeleteManyAsync(filter, ct);
            result = deletion.DeletedCount;
        }

        return result;
    }

    public async Task<bool> DeleteIfUnreferencedAsync(string libraryId,
                                                       string taxonomyVersion,
                                                       string deletingVersion,
                                                       CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(taxonomyVersion);
        ArgumentException.ThrowIfNullOrEmpty(deletingVersion);
        var versionFilter = Builders<LibraryVersionRecord>.Filter.And(
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.LibraryId, libraryId),
            Builders<LibraryVersionRecord>.Filter.Ne(version => version.Version, deletingVersion),
            Builders<LibraryVersionRecord>.Filter.Or(
                Builders<LibraryVersionRecord>.Filter.Eq(version => version.SubjectTaxonomyVersion,
                                                           taxonomyVersion),
                Builders<LibraryVersionRecord>.Filter.Eq(version => version.PublicationState,
                                                           VersionPublicationState.Building)));
        bool versionReferenceExists = await mContext.LibraryVersions.Find(versionFilter)
                                                    .Limit(1)
                                                    .AnyAsync(ct);
        var assignmentFilter = Builders<SubjectAssignmentRecord>.Filter.And(
            Builders<SubjectAssignmentRecord>.Filter.Eq(assignment => assignment.LibraryId, libraryId),
            Builders<SubjectAssignmentRecord>.Filter.Eq(assignment => assignment.TaxonomyVersion,
                                                          taxonomyVersion));
        bool assignmentReferenceExists = await mContext.SubjectAssignments.Find(assignmentFilter)
                                                       .Limit(1)
                                                       .AnyAsync(ct);
        bool result = false;
        if (!versionReferenceExists && !assignmentReferenceExists)
        {
            DeleteResult deletion = await mContext.SubjectCatalogs.DeleteOneAsync(
                                        catalog => catalog.LibraryId == libraryId &&
                                                   catalog.TaxonomyVersion == taxonomyVersion &&
                                                   catalog.PublicationState ==
                                                   SubjectCatalogPublicationState.Candidate,
                                        ct);
            result = deletion.DeletedCount == 1;
        }

        return result;
    }

    public async Task<long> DeleteLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        DeleteResult result = await mContext.SubjectCatalogs.DeleteManyAsync(
                                  catalog => catalog.LibraryId == libraryId,
                                  ct);
        return result.DeletedCount;
    }

    public static string MakeId(string libraryId, string taxonomyVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(taxonomyVersion);
        return $"{libraryId}/{taxonomyVersion}";
    }

    private async Task<IReadOnlyList<SubjectCatalogRecord>> GetManyCoreAsync(
        FilterDefinition<SubjectCatalogRecord> filter,
        CancellationToken ct)
    {
        var result = await mContext.SubjectCatalogs.Find(filter).ToListAsync(ct);
        return result;
    }

    private static FilterDefinition<SubjectCatalogRecord> ImportCatalogFilter(
        string libraryId,
        string taxonomyVersion,
        string importOperationId,
        SubjectCatalogPublicationState publicationState) =>
        Builders<SubjectCatalogRecord>.Filter.And(
            Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.LibraryId, libraryId),
            Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.TaxonomyVersion, taxonomyVersion),
            Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.ImportOperationId, importOperationId),
            Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.PublicationState, publicationState));

    private static FilterDefinition<SubjectCatalogRecord> PublishedOrLegacyFilter() =>
        Builders<SubjectCatalogRecord>.Filter.Or(
            Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.PublicationState,
                                                       SubjectCatalogPublicationState.Published),
            Builders<SubjectCatalogRecord>.Filter.Exists(nameof(SubjectCatalogRecord.PublicationState),
                                                           exists: false));
}
