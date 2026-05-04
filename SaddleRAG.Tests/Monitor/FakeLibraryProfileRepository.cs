// FakeLibraryProfileRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Monitor;

internal sealed class FakeLibraryProfileRepository : ILibraryProfileRepository
{
    private readonly Dictionary<string, LibraryProfile> mProfiles = new();

    public void SetProfile(LibraryProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        mProfiles[Key(profile.LibraryId, profile.Version)] = profile;
    }

    public Task<LibraryProfile?> GetAsync(string libraryId, string version, CancellationToken ct = default) =>
        Task.FromResult(mProfiles.GetValueOrDefault(Key(libraryId, version)));

    public Task UpsertAsync(LibraryProfile profile, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeLibraryProfileRepository: UpsertAsync not supported in this test");

    public Task<long> DeleteAsync(string libraryId, string version, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeLibraryProfileRepository: DeleteAsync not supported in this test");

    public Task<IReadOnlyList<LibraryProfile>> ListAllAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("FakeLibraryProfileRepository: ListAllAsync not supported in this test");

    private static string Key(string libraryId, string version) => $"{libraryId}/{version}";
}
