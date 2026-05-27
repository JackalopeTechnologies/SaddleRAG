// PipelineCounters.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Models.Monitor;

public sealed record PipelineCounters
{
    public int PagesQueued { get; init; }
    public int PagesFetched { get; init; }
    public int PagesClassified { get; init; }
    public int ChunksGenerated { get; init; }
    public int ChunksEmbedded { get; init; }
    public int PagesCompleted { get; init; }
    public int ErrorCount { get; init; }
}
