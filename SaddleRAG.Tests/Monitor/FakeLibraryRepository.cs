// FakeLibraryRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Monitor;

internal sealed class FakeLibraryRepository : ILibraryRepository
{
    private readonly List<LibraryRecord> mLibraries = [];

    private readonly Dictionary<string, LibraryVersionRecord>
        mVersions = new Dictionary<string, LibraryVersionRecord>();

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
            mLibraries[idx] = library;
        else
            mLibraries.Add(library);

        return Task.CompletedTask;
    }

    public Task<LibraryVersionRecord?> GetVersionAsync(string libraryId,
                                                       string version,
                                                       CancellationToken ct = default)
    {
        LibraryVersionRecord? result = null;
        if (mVersions.TryGetValue(VersionKey(libraryId, version), out var found))
            result = found;

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

    public Task<IReadOnlyList<LibraryVersionRecord>> GetVersionsByPublicationStateAsync(
        VersionPublicationState publicationState,
        CancellationToken ct = default)
    {
        var matches = mVersions.Values.Where(v => v.PublicationState == publicationState).ToList();
        return Task.FromResult<IReadOnlyList<LibraryVersionRecord>>(matches);
    }

    public Task UpsertVersionAsync(LibraryVersionRecord versionRecord, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(versionRecord);
        mVersions[VersionKey(versionRecord.LibraryId, versionRecord.Version)] = versionRecord;
        return Task.CompletedTask;
    }

    public Task<DirectoryVersionClaimResult> TryClaimDirectoryVersionAsync(
        LibraryVersionRecord buildingVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(buildingVersion);
        string key = VersionKey(buildingVersion.LibraryId, buildingVersion.Version);
        DirectoryVersionClaimResult result;
        if (!mVersions.TryGetValue(key, out LibraryVersionRecord? existing) ||
            existing.PublicationState == VersionPublicationState.Failed)
        {
            mVersions[key] = buildingVersion;
            result = new DirectoryVersionClaimResult(DirectoryVersionClaimStatus.Acquired,
                                                     RequiresCleanup: existing != null);
        }
        else
        {
            DirectoryVersionClaimStatus status = existing.PublicationState == VersionPublicationState.Published
                                                     ? DirectoryVersionClaimStatus.AlreadyPublished
                                                     : DirectoryVersionClaimStatus.InProgress;
            result = new DirectoryVersionClaimResult(status);
        }

        return Task.FromResult(result);
    }

    public Task<bool> TryPublishDirectoryVersionAsync(LibraryVersionRecord publishedVersion,
                                                      string scanRunId,
                                                      CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(publishedVersion);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);
        string key = VersionKey(publishedVersion.LibraryId, publishedVersion.Version);
        bool result = mVersions.TryGetValue(key, out LibraryVersionRecord? existing)
                      && existing.PublicationState == VersionPublicationState.Building
                      && scanRunId.Equals(existing.ScanRunId, StringComparison.Ordinal)
                      && !existing.CleanupInProgress;
        if (result)
            mVersions[key] = publishedVersion;
        return Task.FromResult(result);
    }

    public Task<bool> TryBeginDirectoryVersionCleanupAsync(string libraryId,
                                                           string version,
                                                           string scanRunId,
                                                           CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);
        string key = VersionKey(libraryId, version);
        bool result = mVersions.TryGetValue(key, out LibraryVersionRecord? existing)
                      && scanRunId.Equals(existing.ScanRunId, StringComparison.Ordinal)
                      && !existing.CleanupInProgress
                      && existing.PublicationState is VersionPublicationState.Building
                          or VersionPublicationState.Published;
        if (result && existing != null)
            mVersions[key] = existing with { CleanupInProgress = true };
        return Task.FromResult(result);
    }

    public Task<bool> TryRecordDirectoryVersionFailureAsync(LibraryVersionRecord failedVersion,
                                                            string scanRunId,
                                                            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(failedVersion);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);
        string key = VersionKey(failedVersion.LibraryId, failedVersion.Version);
        bool result = !mVersions.TryGetValue(key, out LibraryVersionRecord? existing)
                      || scanRunId.Equals(existing.ScanRunId, StringComparison.Ordinal)
                      && existing.CleanupInProgress;
        if (result)
            mVersions[key] = failedVersion;
        return Task.FromResult(result);
    }

    public Task<DeleteVersionResult> DeleteVersionAsync(string libraryId,
                                                        string version,
                                                        CancellationToken ct = default)
    {
        var result = new DeleteVersionResult(VersionsDeleted: 0,
                                             LibraryRowDeleted: false,
                                             CurrentVersionRepointedTo: null
                                            );
        return Task.FromResult(result);
    }

    public Task<long> DeleteAsync(string libraryId, CancellationToken ct = default)
    {
        return Task.FromResult(result: 0L);
    }

    public Task<RenameLibraryResponse> RenameAsync(string oldId, string newId, CancellationToken ct = default)
    {
        var result = new RenameLibraryResponse(RenameLibraryOutcome.NotFound, Counts: null);
        return Task.FromResult(result);
    }

    public Task<RenameLibraryResponse> RenameVersionAsync(string libraryId,
                                                          string oldVersion,
                                                          string newVersion,
                                                          CancellationToken ct = default)
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

    private static string VersionKey(string libraryId, string version) => $"{libraryId}|{version}";
}
