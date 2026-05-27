// IBundleWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Packaging.Internal;

/// <summary>
///     Write surface the exporter targets. Production uses ZipBundleWriter
///     against an on-disk .zip file; tests use a memory-backed variant or
///     the same ZipBundleWriter against a MemoryStream.
/// </summary>
public interface IBundleWriter : IAsyncDisposable
{
    /// <summary>
    ///     Open a new entry in the bundle for writing. The returned stream
    ///     accepts writes until disposed.
    /// </summary>
    Stream OpenEntry(string entryPath);
}
