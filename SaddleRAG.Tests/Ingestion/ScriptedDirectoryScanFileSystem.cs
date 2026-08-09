// ScriptedDirectoryScanFileSystem.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Ingestion.Scanning;

namespace SaddleRAG.Tests.Ingestion;

internal sealed class ScriptedDirectoryScanFileSystem : IDirectoryScanFileSystem
{
    internal ScriptedDirectoryScanFileSystem(StringComparer? pathComparer = null)
    {
        StringComparer comparer = pathComparer ?? StringComparer.OrdinalIgnoreCase;
        mEnumerations = new Dictionary<string, DirectoryEnumerationResult>(comparer);
        mInspections = new Dictionary<string, DirectoryPathResult>(comparer);
        mReads = new Dictionary<string, StableFileReadResult>(comparer);
    }

    private readonly Dictionary<string, DirectoryEnumerationResult> mEnumerations;
    private readonly Dictionary<string, DirectoryPathResult> mInspections;
    private readonly Dictionary<string, StableFileReadResult> mReads;

    public List<string> EnumeratedPaths { get; } = [];

    public List<string> ReadPaths { get; } = [];

    public void SetInspection(string fullPath, DirectoryPathResult result)
    {
        mInspections[fullPath] = result;
    }

    public void SetEnumeration(string fullPath, DirectoryEnumerationResult result)
    {
        mEnumerations[fullPath] = result;
    }

    public void SetRead(string fullPath, StableFileReadResult result)
    {
        mReads[fullPath] = result;
    }

    public DirectoryPathResult InspectPath(string fullPath)
    {
        var result = mInspections.TryGetValue(fullPath, out var configured)
            ? configured
            : new DirectoryPathResult(null, DirectoryScanReasonCodes.RootNotFound, null);
        return result;
    }

    public DirectoryEnumerationResult EnumerateDirectory(string fullPath,
                                                          DirectoryEntrySnapshot? expectedSnapshot = null)
    {
        _ = expectedSnapshot;
        EnumeratedPaths.Add(fullPath);
        var result = mEnumerations.TryGetValue(fullPath, out var configured)
            ? configured
            : new DirectoryEnumerationResult([], DirectoryScanReasonCodes.DirectoryDisappeared, null);
        return result;
    }

    public Task<StableFileReadResult> ReadStableFileAsync(string fullPath,
                                                          long maxFileBytes,
                                                          CancellationToken cancellationToken = default,
                                                          DirectoryEntrySnapshot? expectedSnapshot = null)
    {
        _ = expectedSnapshot;
        cancellationToken.ThrowIfCancellationRequested();
        ReadPaths.Add(fullPath);
        var result = mReads.TryGetValue(fullPath, out var configured)
            ? configured
            : new StableFileReadResult(ReadOnlyMemory<byte>.Empty,
                                       null,
                                       null,
                                       DirectoryScanReasonCodes.FileDisappeared,
                                       new FileNotFoundException("Scripted file was not configured."));
        return Task.FromResult(result);
    }
}
