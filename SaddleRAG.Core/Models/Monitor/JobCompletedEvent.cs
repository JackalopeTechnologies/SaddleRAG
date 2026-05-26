// JobCompletedEvent.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Models.Monitor;

public sealed record JobCompletedEvent
{
    public required string JobId { get; init; }
    public required PipelineCounters FinalCounters { get; init; }
    public required int IndexedPageCount { get; init; }
}
