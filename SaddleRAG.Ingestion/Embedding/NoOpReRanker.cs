// NoOpReRanker.cs

// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial

// Available under AGPLv3 (see LICENSE) or a commercial license

// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.


#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion


namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Passes through vector search results unchanged.
///     Used when re-ranking is disabled or during initial development.
/// </summary>
public class NoOpReRanker : IReRanker

{
    /// <inheritdoc />
    public Task<IReadOnlyList<ReRankResult>> ReRankAsync(string query,
                                                         IReadOnlyList<DocChunk> candidates,
                                                         int maxResults,
                                                         CancellationToken ct = default)

    {
        ArgumentException.ThrowIfNullOrEmpty(query);

        ArgumentNullException.ThrowIfNull(candidates);


        var results = candidates
                      .Take(maxResults)
                      .Select((chunk, index) => new ReRankResult

                                                    {
                                                        Chunk = chunk,

                                                        RelevanceScore = 1.0f - (index * 0.01f)
                                                    }
                             )
                      .ToList();


        return Task.FromResult<IReadOnlyList<ReRankResult>>(results);
    }
}
