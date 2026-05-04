// FakeLibraryRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Monitor;

internal sealed class FakeLibraryRepository : ILibraryRepository
{
    private readonly List<LibraryRecord> mLibraries = new();
    private readonly Dictionary<string, LibraryVersionRecord> mVersions = new();

    public void AddLibrary(LibraryRecord library)
    {
        ArgumentNullException.ThrowIfNull(library);
        mLibraries.Add(library);
    }

    public void AddVersion(LibraryVersionRecord version)
    {
        ArgumentNullException.ThrowIfNull(version);
        mVersions[VersionKey(version.LibraryId, version.Version)] = version;
    }

    public Task<IReadOnlyList<LibraryRecord>> GetAllLibrariesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<LibraryRecord> snapshot = mLibraries.ToList();
        return Task.FromResult(snapshot);
    }

    public Task<LibraryRecord?> GetLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        var match = mLibraries.FirstOrDefault(l => l.Id == libraryId);
        return Task.FromResult(match);
    }

    public Task UpsertLibraryAsync(LibraryRecord library, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        var idx = mLibraries.FindIndex(l => l.Id == library.Id);
        if (idx >= 0)
        {
            mLibraries[idx] = library;
        }
        else
        {
            mLibraries.Add(library);
        }

        return Task.CompletedTask;
    }

    public Task<LibraryVersionRecord?> GetVersionAsync(string libraryId,
                                                       string version,
                                                       CancellationToken ct = default)
    {
        LibraryVersionRecord? result = null;
        if (mVersions.TryGetValue(VersionKey(libraryId, version), out var found))
        {
            result = found;
        }

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<LibraryVersionRecord>> GetVersionsAsync(string libraryId, CancellationToken ct = default)
    {
        var matches = mVersions.Values
                               .Where(v => v.LibraryId == libraryId)
                               .OrderByDescending(v => v.ScrapedAt)
                               .ToList();
        return Task.FromResult<IReadOnlyList<LibraryVersionRecord>>(matches);
    }

    public Task UpsertVersionAsync(LibraryVersionRecord versionRecord, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(versionRecord);
        mVersions[VersionKey(versionRecord.LibraryId, versionRecord.Version)] = versionRecord;
        return Task.CompletedTask;
    }

    public Task<DeleteVersionResult> DeleteVersionAsync(string libraryId,
                                                        string version,
                                                        CancellationToken ct = default)
    {
        var result = new DeleteVersionResult(VersionsDeleted: 0,
                                             LibraryRowDeleted: false,
                                             CurrentVersionRepointedTo: null);
        return Task.FromResult(result);
    }

    public Task<long> DeleteAsync(string libraryId, CancellationToken ct = default)
    {
        return Task.FromResult(0L);
    }

    public Task<RenameLibraryResponse> RenameAsync(string oldId, string newId, CancellationToken ct = default)
    {
        var result = new RenameLibraryResponse(RenameLibraryOutcome.NotFound, Counts: null);
        return Task.FromResult(result);
    }

    public Task SetSuspectAsync(string libraryId,
                                string version,
                                IReadOnlyList<string> reasons,
                                CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task ClearSuspectAsync(string libraryId, string version, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    private static string VersionKey(string libraryId, string version) => $"{libraryId}|{version}";
}
