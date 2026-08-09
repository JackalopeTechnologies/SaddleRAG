// DiffRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using MongoDB.Driver;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion


namespace SaddleRAG.Database.Repositories;

/// <summary>
///     MongoDB implementation of version diff data access.
/// </summary>
public class DiffRepository : IDiffRepository

{
    public DiffRepository(SaddleRagDbContext context)

    {
        mContext = context;
    }


    private readonly SaddleRagDbContext mContext;


    /// <inheritdoc />
    public async Task UpsertDiffAsync(VersionDiffRecord diff, CancellationToken ct = default)

    {
        ArgumentNullException.ThrowIfNull(diff);


        await mContext.VersionDiffs.ReplaceOneAsync(d => d.Id == diff.Id,
                                                    diff,
                                                    new ReplaceOptions { IsUpsert = true },
                                                    ct
                                                   );
    }


    /// <inheritdoc />
    public async Task<VersionDiffRecord?> GetDiffAsync(string libraryId,
                                                       string fromVersion,
                                                       string toVersion,
                                                       CancellationToken ct = default)

    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);

        ArgumentException.ThrowIfNullOrEmpty(fromVersion);

        ArgumentException.ThrowIfNullOrEmpty(toVersion);


        var id = $"{libraryId}/{fromVersion}-to-{toVersion}";

        var result = await mContext.VersionDiffs
                                   .Find(d => d.Id == id)
                                   .FirstOrDefaultAsync(ct);

        return result;
    }


    /// <inheritdoc />
    public async Task<long> DeleteVersionAsync(string libraryId,
                                               string version,
                                               CancellationToken ct = default)

    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);

        ArgumentException.ThrowIfNullOrEmpty(version);


        var filter = Builders<VersionDiffRecord>.Filter.Eq(diff => diff.LibraryId, libraryId) &
                     (Builders<VersionDiffRecord>.Filter.Eq(diff => diff.FromVersion, version) |
                      Builders<VersionDiffRecord>.Filter.Eq(diff => diff.ToVersion, version));
        DeleteResult result = await mContext.VersionDiffs.DeleteManyAsync(filter, ct);
        return result.DeletedCount;
    }


    /// <inheritdoc />
    public async Task<long> DeleteLibraryAsync(string libraryId, CancellationToken ct = default)

    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);


        DeleteResult result = await mContext.VersionDiffs.DeleteManyAsync(
                                  diff => diff.LibraryId == libraryId,
                                  ct);
        return result.DeletedCount;
    }


    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryVersionKey>> GetDistinctLibraryVersionPairsAsync(
        CancellationToken ct = default)

    {
        IReadOnlyList<VersionDiffRecord> diffs = await mContext.VersionDiffs
                                                               .Find(Builders<VersionDiffRecord>.Filter.Empty)
                                                               .ToListAsync(ct);
        var result = diffs.SelectMany(diff => new[]
                           {
                               new LibraryVersionKey(diff.LibraryId, diff.FromVersion),
                               new LibraryVersionKey(diff.LibraryId, diff.ToVersion)
                           })
                          .Distinct()
                          .OrderBy(pair => pair.LibraryId, StringComparer.Ordinal)
                          .ThenBy(pair => pair.Version, StringComparer.Ordinal)
                          .ToList();
        return result;
    }
}
