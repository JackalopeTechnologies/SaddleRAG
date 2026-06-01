// Bm25ShardRepositoryHelpersTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>
///     Pure-helper coverage for <see cref="Bm25ShardRepository" /> — id /
///     filename composition, posting-size estimation, and the serialize /
///     compress round-trips. None of these touch Mongo or GridFS so they
///     run in CI without infrastructure.
/// </summary>
public sealed class Bm25ShardRepositoryHelpersTests
{
    #region MakeShardId

    [Fact]
    public void MakeShardIdComposesThreeSegmentsWithSlashSeparators()
    {
        var id = Bm25ShardRepository.MakeShardId("aerotech-aeroscript", "current", shardIndex: 5);
        Assert.Equal("aerotech-aeroscript/current/5", id);
    }

    [Fact]
    public void MakeShardIdThrowsWhenLibraryIdEmpty()
    {
        Assert.Throws<ArgumentException>(() => Bm25ShardRepository.MakeShardId(string.Empty, "1.0", shardIndex: 0));
    }

    #endregion

    #region MakeShardFileName

    [Fact]
    public void MakeShardFileNameProducesLibVersionShardPath()
    {
        var name = Bm25ShardRepository.MakeShardFileName("lib", "1.0", shardIndex: 3);
        Assert.Equal("lib/1.0/shard-3", name);
    }

    #endregion

    #region MakeTermFileName + SanitizeForFileName

    [Fact]
    public void MakeTermFileNameSanitizesUnsafeCharacters()
    {
        // Term contains dots, colons, parens — Mongo GridFS treats filenames
        // as opaque strings but the sanitizer also defends against shell /
        // CLI consumers that may interpret them. Letters, digits,
        // underscore, hyphen survive; everything else becomes underscore.
        var name = Bm25ShardRepository.MakeTermFileName("lib", "1.0", shardIndex: 2, term: "Foo.Bar::Baz");
        Assert.Equal("lib/1.0/shard-2/term-Foo_Bar__Baz", name);
    }

    [Fact]
    public void SanitizeForFileNamePreservesLettersDigitsUnderscoreHyphen()
    {
        var sanitized = Bm25ShardRepository.SanitizeForFileName("Abc-123_xyz");
        Assert.Equal("Abc-123_xyz", sanitized);
    }

    [Fact]
    public void SanitizeForFileNameReplacesArrowAndPathSeparators()
    {
        // Hyphen is in the keep set, so `->` becomes `-_` (hyphen kept,
        // `>` replaced). Forward / back slashes both become underscore.
        var sanitized = Bm25ShardRepository.SanitizeForFileName("a->b/c\\d");
        Assert.Equal("a-_b_c_d", sanitized);
    }

    [Fact]
    public void SanitizeForFileNamePreservesLengthCharByChar()
    {
        var input = "abc.def(ghi)";
        var sanitized = Bm25ShardRepository.SanitizeForFileName(input);
        Assert.Equal(input.Length, sanitized.Length);
    }

    #endregion

    #region EstimatePostingsSize

    [Fact]
    public void EstimatePostingsSizeIsSumOfChunkIdLengthsPlusOverhead()
    {
        var postings = new List<Bm25Posting>
                           {
                               new Bm25Posting { ChunkId = "ab", TermFrequency = 1 },
                               new Bm25Posting { ChunkId = "abcd", TermFrequency = 1 }
                           };
        // 2 + 4 chars + 2*12 byte overhead = 30
        Assert.Equal(expected: 30, Bm25ShardRepository.EstimatePostingsSize(postings));
    }

    [Fact]
    public void EstimatePostingsSizeIsZeroForEmptyList()
    {
        Assert.Equal(expected: 0, Bm25ShardRepository.EstimatePostingsSize([]));
    }

    #endregion

    #region SerializePostings round-trip

    [Fact]
    public void SerializeDeserializePostingsRoundTripsPreservesContent()
    {
        var original = new List<Bm25Posting>
                           {
                               new Bm25Posting { ChunkId = "chunk-1", TermFrequency = 3 },
                               new Bm25Posting { ChunkId = "chunk-2", TermFrequency = 7 }
                           };

        var bytes = Bm25ShardRepository.SerializePostings(original);
        var restored = Bm25ShardRepository.DeserializePostings(bytes);

        Assert.Equal(original.Count, restored.Count);
        Assert.Equal(original[index: 0].ChunkId, restored[index: 0].ChunkId);
        Assert.Equal(original[index: 0].TermFrequency, restored[index: 0].TermFrequency);
        Assert.Equal(original[index: 1].ChunkId, restored[index: 1].ChunkId);
        Assert.Equal(original[index: 1].TermFrequency, restored[index: 1].TermFrequency);
    }

    [Fact]
    public void SerializePostingsCompressionShrinksRedundantPayloads()
    {
        // Payload with deliberately repetitive content should compress
        // well — guards against accidentally removing the gzip layer.
        var postings = Enumerable.Range(start: 0, count: 500)
                                 .Select(_ => new Bm25Posting { ChunkId = "abcdefghij", TermFrequency = 1 })
                                 .ToList();
        var compressed = Bm25ShardRepository.SerializePostings(postings);
        Assert.True(compressed.Length < postings.Count * 10,
                    $"expected compressed size << raw; got {compressed.Length} bytes for {postings.Count} entries"
                   );
    }

    #endregion

    #region SerializePostingsDictionary round-trip

    [Fact]
    public void SerializeDeserializePostingsDictionaryRoundTripsPreservesKeysAndValues()
    {
        var original = new Dictionary<string, IReadOnlyList<Bm25Posting>>(StringComparer.Ordinal)
                           {
                               ["term-a"] = [new Bm25Posting { ChunkId = "c1", TermFrequency = 2 }],
                               ["term-b"] =
                               [
                                   new Bm25Posting { ChunkId = "c2", TermFrequency = 5 },
                                   new Bm25Posting { ChunkId = "c3", TermFrequency = 1 }
                               ]
                           };

        var bytes = Bm25ShardRepository.SerializePostingsDictionary(original);
        var restored = Bm25ShardRepository.DeserializePostingsDictionary(bytes);

        Assert.Equal(original.Count, restored.Count);
        Assert.True(restored.ContainsKey("term-a"));
        Assert.True(restored.ContainsKey("term-b"));
        Assert.Equal(expected: 2, restored["term-b"].Count);
        Assert.Equal("c2", restored["term-b"][index: 0].ChunkId);
    }

    [Fact]
    public void DeserializePostingsDictionaryReturnsEmptyForEmptyJsonObject()
    {
        var bytes = Bm25ShardRepository.SerializePostingsDictionary(
            new Dictionary<string, IReadOnlyList<Bm25Posting>>(StringComparer.Ordinal));
        var restored = Bm25ShardRepository.DeserializePostingsDictionary(bytes);
        Assert.Empty(restored);
    }

    #endregion

    #region Compress / Decompress

    [Fact]
    public void CompressDecompressRoundTripsExactBytes()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog");
        var compressed = Bm25ShardRepository.Compress(bytes);
        var roundTripped = Bm25ShardRepository.Decompress(compressed);
        Assert.Equal(bytes, roundTripped);
    }

    [Fact]
    public void CompressEmptyBytesRoundTripsToEmptyBytes()
    {
        var compressed = Bm25ShardRepository.Compress([]);
        var roundTripped = Bm25ShardRepository.Decompress(compressed);
        Assert.Empty(roundTripped);
    }

    #endregion
}
