// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Ingestion.Services;

/// <summary>
///     Renames authoritative Mongo identities, then rebuilds disposable
///     vector indexes from the renamed stored chunks.
/// </summary>
public sealed class LibraryRenameService : ILibraryRenameService
{
    public LibraryRenameService(RepositoryFactory repositoryFactory,
                                IVectorSearchProvider vectorSearch)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(vectorSearch);
        mRepositoryFactory = repositoryFactory;
        mVectorSearch = vectorSearch;
    }

    private readonly RepositoryFactory mRepositoryFactory;
    private readonly IVectorSearchProvider mVectorSearch;

    public async Task<RenameLibraryResponse> RenameLibraryAsync(string? profile,
                                                                 string oldLibraryId,
                                                                 string newLibraryId,
                                                                 CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldLibraryId);
        ArgumentException.ThrowIfNullOrEmpty(newLibraryId);
        ILibraryRepository libraries = mRepositoryFactory.GetLibraryRepository(profile);
        RenameLibraryResponse result = await libraries.RenameAsync(oldLibraryId, newLibraryId, ct);
        if (result.Outcome == RenameLibraryOutcome.Renamed)
        {
            try
            {
                await RebuildLibraryIndexesAsync(profile, newLibraryId, ct);
                await mVectorSearch.RemoveLibraryIndexesAsync(profile,
                                                               oldLibraryId,
                                                               CancellationToken.None);
            }
            catch(Exception ex)
            {
                result = result with
                             {
                                 Warning = LibraryMaintenanceWarning(newLibraryId, ex.Message)
                             };
            }
        }

        return result;
    }

    public async Task<RenameLibraryResponse> RenameVersionAsync(string? profile,
                                                                 string libraryId,
                                                                 string oldVersion,
                                                                 string newVersion,
                                                                 CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(oldVersion);
        ArgumentException.ThrowIfNullOrEmpty(newVersion);
        ILibraryRepository libraries = mRepositoryFactory.GetLibraryRepository(profile);
        RenameLibraryResponse result = await libraries.RenameVersionAsync(libraryId,
                                                                           oldVersion,
                                                                           newVersion,
                                                                           ct);
        if (result.Outcome == RenameLibraryOutcome.Renamed)
        {
            try
            {
                await RebuildVersionIndexAsync(profile, libraryId, newVersion, ct);
                await mVectorSearch.RemoveIndexAsync(profile,
                                                     libraryId,
                                                     oldVersion,
                                                     CancellationToken.None);
            }
            catch(Exception ex)
            {
                result = result with
                             {
                                 Warning = VersionMaintenanceWarning(libraryId,
                                                                     newVersion,
                                                                     ex.Message)
                             };
            }
        }

        return result;
    }

    private async Task RebuildLibraryIndexesAsync(string? profile,
                                                  string libraryId,
                                                  CancellationToken ct)
    {
        ILibraryRepository libraries = mRepositoryFactory.GetLibraryRepository(profile);
        IReadOnlyList<LibraryVersionRecord> versions = await libraries.GetVersionsAsync(libraryId, ct);
        foreach(LibraryVersionRecord version in versions.OrderBy(item => item.Version, StringComparer.Ordinal))
            await RebuildVersionIndexAsync(profile, libraryId, version.Version, ct);
    }

    private async Task RebuildVersionIndexAsync(string? profile,
                                                string libraryId,
                                                string version,
                                                CancellationToken ct)
    {
        IChunkRepository chunks = mRepositoryFactory.GetChunkRepository(profile);
        IReadOnlyList<DocChunk> stored = await chunks.GetChunksAsync(libraryId, version, ct);
        IReadOnlyList<DocChunk> embedded = stored.Where(chunk => chunk.Embedding != null).ToList();
        await mVectorSearch.IndexChunksAsync(profile, libraryId, version, embedded, ct);
    }

    private static string LibraryMaintenanceWarning(string libraryId, string detail) =>
        $"The MongoDB rename completed, but vector index maintenance failed: {detail} " +
        $"Run reembed_library for library '{libraryId}' to rebuild its searchable indexes.";

    private static string VersionMaintenanceWarning(string libraryId,
                                                    string version,
                                                    string detail) =>
        $"The MongoDB rename completed, but vector index maintenance failed: {detail} " +
        $"Run reembed_library for library '{libraryId}' version '{version}' to rebuild its searchable index.";
}
