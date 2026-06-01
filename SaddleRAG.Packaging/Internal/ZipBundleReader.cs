// ZipBundleReader.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.IO.Compression;

#endregion

namespace SaddleRAG.Packaging.Internal;

/// <summary>
///     <see cref="IBundleReader" /> backed by a <see cref="ZipArchive" /> opened
///     from an on-disk .srlib.zip file.
/// </summary>
public sealed class ZipBundleReader : IBundleReader
{
    public ZipBundleReader(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        mArchive = ZipFile.OpenRead(path);
    }

    private readonly ZipArchive mArchive;

    public bool HasEntry(string entryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryPath);
        return mArchive.GetEntry(entryPath) is not null;
    }

    public Stream OpenEntry(string entryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryPath);
        var entry = mArchive.GetEntry(entryPath)
                    ?? throw new InvalidOperationException($"Bundle missing required entry '{entryPath}'");
        return entry.Open();
    }

    public long GetEntryLength(string entryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryPath);
        var entry = mArchive.GetEntry(entryPath)
                    ?? throw new InvalidOperationException($"Bundle missing required entry '{entryPath}'");
        return entry.Length;
    }

    public void Dispose() => mArchive.Dispose();
}
