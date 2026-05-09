// OllamaReRanker.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Generate-mode LLM reranker. Per-pair (query, document) scoring
///     against the configured ReRankingModel (default phi4-mini:3.8b).
///     Each candidate gets a single Ollama call returning a continuous
///     float in [0, 1] driven by a 3-shot corpus-anchored prompt.
///     Replaces the legacy 5-bucket categorical implementation that
///     plateaued at HIGH for nearly every candidate when driven by
///     qwen3:1.7b. Calls run concurrently via Task.WhenAll — Ollama
///     serializes at the daemon for one loaded model so worst case
///     matches sequential latency, while num_parallel &gt; 1
///     installations get a 2-4x speedup.
/// </summary>
public class OllamaReRanker : IReRanker
{
    public OllamaReRanker(IOptions<OllamaSettings> settings,
                          ILogger<OllamaReRanker> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        mSettings = settings.Value;
        mLogger = logger;
        mClient = new OllamaApiClient(new Uri(mSettings.Endpoint));
        mLogger.LogDebug("OllamaReRanker initialized with prompt {Version}", PromptVersion);
    }

    private readonly OllamaApiClient mClient;
    private readonly ILogger<OllamaReRanker> mLogger;
    private readonly OllamaSettings mSettings;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReRankResult>> ReRankAsync(string query,
                                                               IReadOnlyList<DocChunk> candidates,
                                                               int maxResults,
                                                               CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        ArgumentNullException.ThrowIfNull(candidates);

        IReadOnlyList<ReRankResult> result;
        if (candidates.Count == 0)
            result = Array.Empty<ReRankResult>();
        else
            result = await ScoreCandidatesAsync(query, candidates, maxResults, ct);
        return result;
    }

    private async Task<IReadOnlyList<ReRankResult>> ScoreCandidatesAsync(string query,
                                                                         IReadOnlyList<DocChunk> candidates,
                                                                         int maxResults,
                                                                         CancellationToken ct)
    {
        var tasks = candidates
                    .Select(chunk => ScoreCandidateAsync(query, chunk, ct))
                    .ToArray();

        var scored = await Task.WhenAll(tasks);
        var ordered = scored.OrderByDescending(r => r.RelevanceScore).Take(maxResults).ToList();
        return ordered;
    }

    private async Task<ReRankResult> ScoreCandidateAsync(string query, DocChunk chunk, CancellationToken ct)
    {
        var score = await ScoreOneAsync(query, chunk.Content, ct);
        var result = new ReRankResult
                         {
                             Chunk = chunk,
                             RelevanceScore = score
                         };
        return result;
    }

    private async Task<float> ScoreOneAsync(string query, string content, CancellationToken ct)
    {
        var prompt = BuildPrompt(query, TruncateDocument(content));
        var responseText = await CallOllamaAsync(prompt, ct);
        var score = ParseScore(responseText);
        return score;
    }

    private async Task<string> CallOllamaAsync(string prompt, CancellationToken ct)
    {
        var responseBuilder = new StringBuilder();
        try
        {
            var request = new GenerateRequest
                              {
                                  Model = mSettings.ReRankingModel,
                                  Prompt = prompt,
                                  Stream = true,
                                  Options = new RequestOptions { Temperature = 0f }
                              };
            await foreach(var token in mClient.GenerateAsync(request, ct))
            {
                if (responseBuilder.Length < MaxResponseChars)
                    responseBuilder.Append(token?.Response ?? string.Empty);
            }
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            mLogger.LogWarning(ex, "Reranker call failed; treating as score 0");
        }

        return responseBuilder.ToString().Trim();
    }

    private static string TruncateDocument(string content)
    {
        var result = content.Length <= MaxDocumentChars ? content : content[..MaxDocumentChars];
        return result;
    }

    /// <summary>
    ///     Build the 3-shot corpus-anchored prompt for relevance scoring.
    ///     Anchor cases lock in the calibration scale: an exact-name
    ///     match against the canonical class doc → 0.95; a sibling-class
    ///     mention in a related-but-not-target doc → 0.55; an entirely
    ///     unrelated doc from a different library → 0.05. Public so unit
    ///     tests can pin the prompt format without spinning Ollama.
    /// </summary>
    public static string BuildPrompt(string query, string document)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        ArgumentNullException.ThrowIfNull(document);

        var prompt = $$"""
                       You are a documentation relevance scorer. Given a search query and a documentation snippet, output ONLY a single decimal number between 0.0 and 1.0 representing how well the snippet answers the query. No explanation, no other text.

                       Scale:
                       0.9-1.0  Perfect match — the snippet IS the documentation for what the query asks
                       0.6-0.8  Strong match — closely related class, method, or concept
                       0.3-0.5  Weak match — mentions related ideas but not the focus
                       0.0-0.2  Off topic

                       Examples:

                       Query: FastLineRenderableSeries class
                       Document: FastLineRenderableSeries Class — Defines a Line renderable series, supporting solid, stroked (thickness 1+) lines, dashed lines and optional Point-markers. A RenderableSeries has an IDataSeries data-source.
                       Score: 0.95

                       Query: FastLineRenderableSeries class
                       Document: Spline Line Series — smoothing algorithm that gives charts a smooth look. Uses SplineLineRenderableSeries type to render a line with cubic-spline interpolation.
                       Score: 0.55

                       Query: FastLineRenderableSeries class
                       Document: How to clone the Math.NET Spatial source code from GitHub. Run git clone https://github.com/mathnet/mathnet-spatial.git, then open the .sln in Visual Studio.
                       Score: 0.05

                       Now score this:

                       Query: {{query}}
                       Document: {{document}}
                       Score:
                       """;
        return prompt;
    }

    /// <summary>
    ///     Tolerant parser. The model is asked to emit only a number but
    ///     occasionally adds prefixes ("Relevance: 0.85") or extra text
    ///     ("0.85 (high confidence)"). Extracts the first float-shaped
    ///     token from the response and clamps to [0, 1]. Returns 0 if no
    ///     float is found (empty response, model confused, etc.).
    /// </summary>
    public static float ParseScore(string responseText)
    {
        ArgumentNullException.ThrowIfNull(responseText);

        var score = 0f;
        var match = smFloatRegex.Match(responseText);
        if (match.Success && float.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
            score = Math.Clamp(raw, MinScore, MaxScore);

        return score;
    }

    /// <summary>
    ///     Bumped whenever the prompt template changes meaningfully.
    ///     Currently informational only (rerank scores aren't cached) but
    ///     reserved for future bench-harness diffing.
    /// </summary>
    public const string PromptVersion = "v2-continuous";

    private const int MaxResponseChars = 256;
    private const int MaxDocumentChars = 2000;
    private const float MinScore = 0f;
    private const float MaxScore = 1f;

    // Matches floats like "0.85", "0.0", ".97", "1", "1.0".
    private static readonly Regex smFloatRegex = new Regex(@"\d*\.\d+|\d+(?:\.\d+)?", RegexOptions.Compiled);
}
