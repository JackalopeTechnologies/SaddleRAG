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
        SubjectCatalogRecord? result = await mContext.SubjectCatalogs
                                                     .Find(catalog => catalog.LibraryId == libraryId)
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
        IReadOnlyList<string> protectedTaxonomies = versions
                                                    .Where(version =>
                                                               version.PublicationState ==
                                                               VersionPublicationState.Published &&
                                                               !string.Equals(version.Version,
                                                                              deletingVersion,
                                                                              StringComparison.Ordinal) &&
                                                               !string.IsNullOrWhiteSpace(
                                                                   version.SubjectTaxonomyVersion))
                                                    .Select(version => version.SubjectTaxonomyVersion)
                                                    .OfType<string>()
                                                    .Distinct(StringComparer.Ordinal)
                                                    .ToList();
        FilterDefinition<SubjectCatalogRecord> filter = Builders<SubjectCatalogRecord>.Filter.And(
            Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.LibraryId, libraryId),
            Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.ScanRunId, scanRunId),
            Builders<SubjectCatalogRecord>.Filter.Nin(catalog => catalog.TaxonomyVersion,
                                                       protectedTaxonomies));
        DeleteResult result = await mContext.SubjectCatalogs.DeleteManyAsync(filter, ct);
        return result.DeletedCount;
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
}
