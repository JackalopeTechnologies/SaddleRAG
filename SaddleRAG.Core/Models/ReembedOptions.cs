// ReembedOptions.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Caller-supplied options for reembed_library. Defaults match the
///     "common case": re-embed every stored chunk via the currently
///     configured IEmbeddingProvider and reload the vector index.
/// </summary>
public record ReembedOptions
{
    /// <summary>
    ///     When true, the tool reports what would change without writing
    ///     new embeddings to MongoDB or reloading the vector index.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    ///     Optional cap for spot-checking a large library — process only
    ///     this many chunks. When null, every chunk in (library, version)
    ///     is re-embedded.
    /// </summary>
    public int? MaxChunks { get; init; }
}
