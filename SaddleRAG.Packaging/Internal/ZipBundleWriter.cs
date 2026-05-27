// ZipBundleWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.IO.Compression;

#endregion

namespace SaddleRAG.Packaging.Internal;

public sealed class ZipBundleWriter : IBundleWriter
{
    public ZipBundleWriter(Stream output, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        mArchive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: leaveOpen);
    }

    private readonly ZipArchive mArchive;

    public Stream OpenEntry(string entryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryPath);
        var entry = mArchive.CreateEntry(entryPath, CompressionLevel.Optimal);
        return entry.Open();
    }

    public ValueTask DisposeAsync()
    {
        mArchive.Dispose();
        return ValueTask.CompletedTask;
    }
}
