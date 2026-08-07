// DocumentProvenance.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Citation metadata carried from a local document revision into pages
///     and chunks. Absolute filesystem roots are intentionally excluded.
/// </summary>
public record DocumentProvenance
{
    public required string DocumentId { get; init; }

    public required string RevisionId { get; init; }

    public required string SourceUri { get; init; }

    public required string RelativePath { get; init; }

    /// <summary>URL discovered by the crawler before retries or redirects.</summary>
    public string? OriginalUrl { get; init; }

    /// <summary>Exact URL supplied to the successful HTTP navigation.</summary>
    public string? AttemptedUrl { get; init; }

    /// <summary>Final response URL after redirects.</summary>
    public string? FinalUrl { get; init; }

    public int? PageStart { get; init; }

    public int? PageEnd { get; init; }

    public string? Heading { get; init; }
}
