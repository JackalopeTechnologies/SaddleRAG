// IBundleReader.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Packaging.Internal;

/// <summary>
///     Read surface the importer targets. Production uses <see cref="ZipBundleReader" />
///     against an on-disk .srlib.zip file; tests may use memory-backed variants
///     or the same reader against a temp file.
/// </summary>
public interface IBundleReader : IDisposable
{
    /// <summary>
    ///     Returns true if the bundle contains an entry at <paramref name="entryPath" />.
    /// </summary>
    bool HasEntry(string entryPath);

    /// <summary>
    ///     Opens the entry at <paramref name="entryPath" /> for reading.
    ///     Throws <see cref="InvalidOperationException" /> if the entry does not exist.
    /// </summary>
    Stream OpenEntry(string entryPath);

    /// <summary>
    ///     Returns the uncompressed byte length of the entry at <paramref name="entryPath" />.
    ///     Throws <see cref="InvalidOperationException" /> if the entry does not exist.
    /// </summary>
    long GetEntryLength(string entryPath);
}
