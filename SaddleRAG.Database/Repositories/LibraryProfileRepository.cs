// LibraryProfileRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using MongoDB.Driver;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Database.Repositories;

/// <summary>
///     MongoDB-backed implementation of ILibraryProfileRepository.
///     Profiles are keyed by (LibraryId, Version) via a composite document id.
/// </summary>
public class LibraryProfileRepository : ILibraryProfileRepository
{
    public LibraryProfileRepository(SaddleRagDbContext context)
    {
        mContext = context;
    }

    private readonly SaddleRagDbContext mContext;

    /// <inheritdoc />
    public async Task<LibraryProfile?> GetAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var id = MakeId(libraryId, version);
        var result = await mContext.LibraryProfiles
                                   .Find(p => p.Id == id)
                                   .FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task UpsertAsync(LibraryProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await mContext.LibraryProfiles.ReplaceOneAsync(p => p.Id == profile.Id,
                                                       profile,
                                                       new ReplaceOptions { IsUpsert = true },
                                                       ct
                                                      );
    }

    /// <inheritdoc />
    public async Task<long> DeleteAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var id = MakeId(libraryId, version);
        var result = await mContext.LibraryProfiles.DeleteOneAsync(p => p.Id == id, ct);
        return result.DeletedCount;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryProfile>> ListAllAsync(CancellationToken ct = default)
    {
        var profiles = await mContext.LibraryProfiles
                                     .Find(FilterDefinition<LibraryProfile>.Empty)
                                     .ToListAsync(ct);
        return profiles;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryVersionKey>> GetDistinctLibraryVersionPairsAsync(
        CancellationToken ct = default)
    {
        var grouped = await mContext.LibraryProfiles
                                    .Aggregate()
                                    .Group(p => new { p.LibraryId, p.Version },
                                           g => new { g.Key.LibraryId, g.Key.Version }
                                          )
                                    .ToListAsync(ct);
        var result = grouped.Select(g => new LibraryVersionKey(g.LibraryId, g.Version)).ToList();
        return result;
    }

    /// <summary>
    ///     Compose the document id used as the primary key for a profile.
    ///     Public so callers building new LibraryProfile records can use the
    ///     same convention without duplicating the format.
    /// </summary>
    public static string MakeId(string libraryId, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        return $"{libraryId}/{version}";
    }
}
