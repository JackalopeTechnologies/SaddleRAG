// PageRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using MongoDB.Driver;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Database.Repositories;

/// <summary>
///     MongoDB implementation of page record data access.
/// </summary>
public class PageRepository : IPageRepository
{
    public PageRepository(SaddleRagDbContext context)
    {
        mContext = context;
    }

    private readonly SaddleRagDbContext mContext;

    /// <inheritdoc />
    public async Task UpsertPageAsync(PageRecord page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var filter = BuildUpsertFilter(page);

        await mContext.Pages.ReplaceOneAsync(filter,
                                             page,
                                             new ReplaceOptions { IsUpsert = true },
                                             ct
                                            );
    }

    /// <summary>
    ///     Filters the upsert by the synthetic <see cref="PageRecord.Id" />,
    ///     which is the canonical URL hash. Filtering by raw
    ///     <see cref="PageRecord.Url" /> collides with Mongo's unique
    ///     <c>_id</c> index whenever the same logical page is fetched under
    ///     two URL variants (e.g. <c>/License</c> and <c>/License.html</c>) —
    ///     the URL filter misses the existing doc, the upsert switches to
    ///     insert, and the duplicate canonical Id is rejected.
    /// </summary>
    internal static FilterDefinition<PageRecord> BuildUpsertFilter(PageRecord page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var filter = Builders<PageRecord>.Filter.Eq(p => p.Id, page.Id);
        return filter;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PageRecord>> GetPagesAsync(string libraryId,
                                                               string version,
                                                               CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter = Builders<PageRecord>.Filter.And(Builders<PageRecord>.Filter.Eq(p => p.LibraryId, libraryId),
                                                     Builders<PageRecord>.Filter.Eq(p => p.Version, version)
                                                    );

        var pages = await mContext.Pages.Find(filter).ToListAsync(ct);
        return pages;
    }

    /// <inheritdoc />
    public async Task<PageRecord?> GetPageByUrlAsync(string libraryId,
                                                     string version,
                                                     string url,
                                                     CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(url);

        var filter = Builders<PageRecord>.Filter.And(Builders<PageRecord>.Filter.Eq(p => p.LibraryId, libraryId),
                                                     Builders<PageRecord>.Filter.Eq(p => p.Version, version),
                                                     Builders<PageRecord>.Filter.Eq(p => p.Url, url)
                                                    );

        var result = await mContext.Pages.Find(filter).FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<int> GetPageCountAsync(string libraryId,
                                             string version,
                                             CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter = Builders<PageRecord>.Filter.And(Builders<PageRecord>.Filter.Eq(p => p.LibraryId, libraryId),
                                                     Builders<PageRecord>.Filter.Eq(p => p.Version, version)
                                                    );

        var count = (int) await mContext.Pages.CountDocumentsAsync(filter, cancellationToken: ct);
        return count;
    }

    /// <inheritdoc />
    public async Task<long> DeleteAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter = Builders<PageRecord>.Filter.And(Builders<PageRecord>.Filter.Eq(p => p.LibraryId, libraryId),
                                                     Builders<PageRecord>.Filter.Eq(p => p.Version, version)
                                                    );
        var result = await mContext.Pages.DeleteManyAsync(filter, ct);
        return result.DeletedCount;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryVersionKey>> GetDistinctLibraryVersionPairsAsync(
        CancellationToken ct = default)
    {
        var grouped = await mContext.Pages
                                    .Aggregate()
                                    .Group(p => new { p.LibraryId, p.Version },
                                           g => new { g.Key.LibraryId, g.Key.Version }
                                          )
                                    .ToListAsync(ct);
        var result = grouped.Select(g => new LibraryVersionKey(g.LibraryId, g.Version)).ToList();
        return result;
    }
}
