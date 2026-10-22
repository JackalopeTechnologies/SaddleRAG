// Bm25Scorer.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.RegularExpressions;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Standard BM25 scoring against a sharded postings store. The scorer
///     extracts query terms, asks the supplied <see cref="IBm25TermLookup" />
///     to pre-load whichever shards back those terms, then computes scores
///     synchronously against the cached postings. Used by the hybrid
///     retrieval path to blend keyword scores with vector cosine
///     similarity, especially valuable for identifier queries where
///     embedding similarity alone struggles to discriminate between
///     "mentions term" and "is canonical reference for term".
///     k1 = 1.5, b = 0.75 are the standard defaults; configurable for
///     bench tuning later if needed.
/// </summary>
public static class Bm25Scorer
{
    /// <summary>
    ///     Score every chunk against the query. Returns a dictionary
    ///     keyed by chunk id; chunks with score 0 are omitted. Scores
    ///     are not normalized — caller can normalize to [0,1] for
    ///     blending with vector cosine.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, double>> ScoreAsync(IBm25TermLookup termLookup,
                                                                             Bm25Stats stats,
                                                                             string query,
                                                                             CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(termLookup);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentException.ThrowIfNullOrEmpty(query);

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        if (stats.DocumentCount > 0)
        {
            var queryTerms = ExtractQueryTerms(query);
            await termLookup.PreloadAsync(queryTerms, ct);
            ScoreNonEmptyIndex(termLookup, stats, queryTerms, scores);
        }

        return scores;
    }

    /// <summary>
    ///     Score and return the top-N chunk ids ordered by score desc.
    ///     Convenience wrapper around <see cref="ScoreAsync" />.
    /// </summary>
    public static async Task<IReadOnlyList<(string ChunkId, double Score)>> TopNAsync(IBm25TermLookup termLookup,
        Bm25Stats stats,
        string query,
        int n,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(termLookup);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentException.ThrowIfNullOrEmpty(query);

        if (n <= 0)
            throw new ArgumentOutOfRangeException(nameof(n), n, "n must be positive");

        var scores = await ScoreAsync(termLookup, stats, query, ct);
        var ordered = scores.OrderByDescending(kv => kv.Value)
                            .Take(n)
                            .Select(kv => (kv.Key, kv.Value))
                            .ToList();
        return ordered;
    }

    private static void ScoreNonEmptyIndex(IBm25TermLookup termLookup,
                                           Bm25Stats stats,
                                           IReadOnlyList<string> queryTerms,
                                           Dictionary<string, double> scores)
    {
        var avgLength = stats.AverageDocLength > 0 ? stats.AverageDocLength : 1.0;

        foreach(var term in queryTerms)
        {
            var postings = termLookup.GetPostings(term);
            if (postings.Count > 0)
            {
                var idf = ComputeIdf(stats.DocumentCount, postings.Count);
                foreach(var posting in postings)
                    AddTermScore(stats, scores, posting, idf, avgLength);
            }
        }
    }

    private static void AddTermScore(Bm25Stats stats,
                                     Dictionary<string, double> scores,
                                     Bm25Posting posting,
                                     double idf,
                                     double avgLength)
    {
        var docLength = stats.DocLengths.TryGetValue(posting.ChunkId, out var len) ? len : (int) avgLength;
        var termFreq = posting.TermFrequency;
        var lengthNorm = 1.0 - B + (B * (docLength / avgLength));
        var numerator = termFreq * (K1 + 1.0);
        var denominator = termFreq + (K1 * lengthNorm);
        var contribution = idf * (numerator / denominator);
        scores[posting.ChunkId] =
            scores.TryGetValue(posting.ChunkId, out var current) ? current + contribution : contribution;
    }

    private static double ComputeIdf(int documentCount, int postingCount)
    {
        var numerator = documentCount - postingCount + 0.5;
        var denominator = postingCount + 0.5;
        var ratio = (numerator / denominator) + 1.0;
        var result = Math.Log(ratio);
        return result;
    }

    /// <summary>
    ///     Tokenize a query the SAME way <see cref="Bm25IndexBuilder" />
    ///     tokenizes chunks. Lowercased prose tokens AND identifier tokens
    ///     via <see cref="IdentifierTokenizer.EmitRawAndLowercase" />
    ///     (raw casing plus a lowercase mirror so case-mismatched dotted
    ///     queries match indexed mixed-case identifiers), deduped. Common prose
    ///     connecting words are omitted when useful terms remain. Explicitly
    ///     quoted terms and callable names remain searchable, as do queries
    ///     consisting entirely of connecting words.
    /// </summary>
    public static IReadOnlyList<string> ExtractQueryTerms(string query)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);

        var terms = new List<string>();

        foreach(var match in smProseTokenRegex.Matches(query).Where(m => m.Value.Length >= MinTokenLength))
            terms.Add(match.Value.ToLowerInvariant());

        foreach(var token in IdentifierTokenizer.EmitRawAndLowercase(query, MinTokenLength))
            terms.Add(token);

        var distinct = FilterProseTerms(query, terms.Distinct(StringComparer.Ordinal).ToList());
        return distinct;
    }

    private static IReadOnlyList<string> FilterProseTerms(string query, IReadOnlyList<string> terms)
    {
        // A rare occurrence of "about" in a small documentation collection
        // must not outweigh the words describing the requested operation.
        // Keep the index intact: this only filters query-side keyword votes;
        // semantic search still receives the complete original question.
        var explicitTerms = smExplicitTermRegex.Matches(query)
                                              .SelectMany(match => IdentifierTokenizer.EmitRawAndLowercase(
                                                  match.Value, MinTokenLength))
                                              .ToHashSet(StringComparer.Ordinal);
        var meaningful = terms.Where(term => !smProseStopWords.Contains(term) || explicitTerms.Contains(term)).ToList();
        IReadOnlyList<string> result = meaningful.Count > 0 ? meaningful : terms;
        return result;
    }

    private const int MinTokenLength = 2;
    private const double K1 = 1.5;
    private const double B = 0.75;

    // Deliberately exclude technical words (return, value, argument, etc.),
    // quantifiers (any, all) and negation (not, without). The symbol extractor's
    // broader stoplist would discard useful search intent and is unsuitable here.
    private const string ProseStopWordsA =
        "about an are as at be been being by do does for from had has have how in is it its of on";
    private const string ProseStopWordsB =
        "that the their there these this those to was were what when where which who with would you your";

    private static readonly HashSet<string> smProseStopWords =
        new((ProseStopWordsA + " " + ProseStopWordsB).Split(' '), StringComparer.OrdinalIgnoreCase);

    // Quotes/backticks express literal terms; parentheses express a callable
    // name. Both must retain names such as About or Where despite prose filtering.
    private static readonly Regex smExplicitTermRegex = new(
        """(?<quote>["'`]).+?\k<quote>|\b[A-Za-z_][A-Za-z0-9_]*\s*\(""", RegexOptions.Compiled);

    private static readonly Regex smProseTokenRegex = new Regex("[A-Za-z][A-Za-z0-9]+", RegexOptions.Compiled);
}
