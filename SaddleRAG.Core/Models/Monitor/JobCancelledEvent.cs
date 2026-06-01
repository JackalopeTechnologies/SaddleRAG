// JobCancelledEvent.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models.Monitor;

public sealed record JobCancelledEvent
{
    public required string JobId { get; init; }
    public required PipelineCounters PartialCounters { get; init; }
}
