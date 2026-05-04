// ShardedBm25TermLookup.cs
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
///     Pre-loading shard-routed term lookup over <see cref="IBm25ShardRepository" />.
///     One instance per query path; not thread-safe. The scorer creates
///     one, calls <see cref="PreloadAsync" /> with the query terms, then
///     scores synchronously against the cached postings.
/// </summary>
public class ShardedBm25TermLookup : IBm25TermLookup
{
    public ShardedBm25TermLookup(IBm25ShardRepository shardRepository,
                                 string libraryId,
                                 string version,
                                 int shardCount)
    {
        ArgumentNullException.ThrowIfNull(shardRepository);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        if (shardCount < 1)
            throw new ArgumentOutOfRangeException(nameof(shardCount), shardCount, "shardCount must be >= 1");

        mShardRepository = shardRepository;
        mLibraryId = libraryId;
        mVersion = version;
        mShardCount = shardCount;
    }

    private readonly string mLibraryId;

    private readonly Dictionary<string, IReadOnlyList<Bm25Posting>> mPostingsCache =
        new Dictionary<string, IReadOnlyList<Bm25Posting>>(StringComparer.Ordinal);

    private readonly Dictionary<int, Bm25Shard?> mShardCache = new Dictionary<int, Bm25Shard?>();
    private readonly int mShardCount;

    private readonly IBm25ShardRepository mShardRepository;
    private readonly string mVersion;

    /// <inheritdoc />
    public async Task PreloadAsync(IReadOnlyList<string> queryTerms, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queryTerms);

        var shardsToLoad = queryTerms
                           .Where(t => !string.IsNullOrEmpty(t))
                           .Select(t => Bm25IndexBuilder.ShardIndexFor(t, mShardCount))
                           .Distinct()
                           .Where(idx => !mShardCache.ContainsKey(idx))
                           .ToList();

        foreach(var shardIndex in shardsToLoad)
            mShardCache[shardIndex] = await mShardRepository.GetShardAsync(mLibraryId, mVersion, shardIndex, ct);

        foreach(var term in queryTerms.Where(t => !string.IsNullOrEmpty(t) && !mPostingsCache.ContainsKey(t)))
            mPostingsCache[term] = await ResolveTermAsync(term, ct);
    }

    /// <inheritdoc />
    public IReadOnlyList<Bm25Posting> GetPostings(string term)
    {
        ArgumentException.ThrowIfNullOrEmpty(term);
        var result = mPostingsCache.TryGetValue(term, out var found) ? found : [];
        return result;
    }

    private async Task<IReadOnlyList<Bm25Posting>> ResolveTermAsync(string term, CancellationToken ct)
    {
        var shardIndex = Bm25IndexBuilder.ShardIndexFor(term, mShardCount);
        var shard = mShardCache.TryGetValue(shardIndex, out var cached) ? cached : null;
        var result = shard != null
                         ? await mShardRepository.LoadPostingsAsync(shard, term, ct)
                         : (IReadOnlyList<Bm25Posting>) [];
        return result;
    }
}
