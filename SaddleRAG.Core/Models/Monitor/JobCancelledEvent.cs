// JobCancelledEvent.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Models.Monitor;

public sealed record JobCancelledEvent
{
    public required string JobId { get; init; }
    public required PipelineCounters PartialCounters { get; init; }
}
