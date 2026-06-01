// StageTimings.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Per-stage timing aggregates emitted by the dry-run pipeline.
///     Totals are sums of every sample recorded by the stage; sample
///     counts let consumers derive averages. Time units are milliseconds
///     throughout.
/// </summary>
public record StageTimings
{
    public required long TotalFetchMs { get; init; }
    public required int FetchSampleCount { get; init; }
    public required long TotalClassifyMs { get; init; }
    public required int ClassifySampleCount { get; init; }
    public required long TotalChunkMs { get; init; }
    public required int ChunkSampleCount { get; init; }
    public required long TotalEmbedMs { get; init; }
    public required int EmbedBatchCount { get; init; }

    public static StageTimings Empty { get; } = new()
                                                    {
                                                        TotalFetchMs = 0,
                                                        FetchSampleCount = 0,
                                                        TotalClassifyMs = 0,
                                                        ClassifySampleCount = 0,
                                                        TotalChunkMs = 0,
                                                        ChunkSampleCount = 0,
                                                        TotalEmbedMs = 0,
                                                        EmbedBatchCount = 0
                                                    };
}
