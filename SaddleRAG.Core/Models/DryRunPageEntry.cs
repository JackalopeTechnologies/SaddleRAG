// DryRunPageEntry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     A single page that was visited during a dry run.
/// </summary>
public record DryRunPageEntry

{
    public required string Url { get; init; }

    public required int OutOfScopeDepth { get; init; }

    public required bool InScope { get; init; }

    public required int ContentBytes { get; init; }

    public required int LinksFound { get; init; }

    /// <summary>
    ///     Substantial content nodes present after DOMContentLoaded.
    ///     -1 when the Playwright evaluation failed.
    /// </summary>

    public required int ContentNodesAtDom { get; init; }

    /// <summary>
    ///     Substantial content nodes present after LoadState.Load.
    ///     -1 when the Playwright evaluation failed or load wait was skipped.
    /// </summary>

    public required int ContentNodesAtLoad { get; init; }
}
