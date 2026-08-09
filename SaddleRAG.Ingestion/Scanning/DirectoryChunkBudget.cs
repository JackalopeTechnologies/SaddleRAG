// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Tracks aggregate chunks generated for one directory candidate.</summary>
internal sealed class DirectoryChunkBudget
{
    internal DirectoryChunkBudget(int maxChunkCount)
    {
        if (maxChunkCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxChunkCount));
        mMaxChunkCount = maxChunkCount;
    }

    private readonly int mMaxChunkCount;

    internal int ChunkCount { get; private set; }

    internal void Add(int chunkCount, string relativePath)
    {
        if (chunkCount < 0)
            throw new ArgumentOutOfRangeException(nameof(chunkCount));
        if (chunkCount > mMaxChunkCount - ChunkCount)
        {
            throw new DirectoryIngestionException(
                DirectoryScanReasonCodes.ChunkCountLimitExceeded,
                "The generated chunks exceed the configured aggregate library limit.",
                relativePath);
        }

        ChunkCount += chunkCount;
    }
}
