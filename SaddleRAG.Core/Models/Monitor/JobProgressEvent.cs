// JobProgressEvent.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Models.Monitor;

/// <summary>
///     Live progress tick for a job that reports incremental counts
///     (rechunk → chunks, dry-run → pages, deps-index → packages).
/// </summary>
public sealed record JobProgressEvent
{
    public required string JobId { get; init; }
    public required int ItemsProcessed { get; init; }
    public required int ItemsTotal { get; init; }
    public required string ItemsLabel { get; init; }
}
