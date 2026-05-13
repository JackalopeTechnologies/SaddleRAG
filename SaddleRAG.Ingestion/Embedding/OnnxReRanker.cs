// OnnxReRanker.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Cross-encoder reranker running an ONNX model in-process via
///     Microsoft.ML.OnnxRuntime. Loads the active reranker entry from
///     <see cref="OnnxSettings.RerankerModels" /> and scores (query, doc)
///     pairs in batched forward passes. Phase 1 spike confirmed this path
///     against mxbai-rerank-base-v1 (SentencePiece + DeBERTa-v2).
///     If the active reranker resolves to null (Onnx.ActiveRerankerModel
///     set to "none" or registry empty), this class behaves like
///     <see cref="NoOpReRanker" /> — passes candidates through with a
///     synthetic descending score.
/// </summary>
public sealed class OnnxReRanker : IReRanker, IDisposable
{
    public OnnxReRanker(IOptions<OnnxSettings> settings,
                        OnnxRuntimeCapabilities capabilities,
                        ILogger<OnnxReRanker> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(logger);

        mSettings = settings.Value;
        mLogger = logger;
        mEntry = mSettings.GetActiveRerankerModel();

        if (mEntry == null)
        {
            mLogger.LogInformation(
                "OnnxReRanker disabled (Onnx.ActiveRerankerModel resolved to null). Pass-through behavior active."
            );
        }
        else
        {
            string modelDir = Path.Combine(mSettings.ModelsDir, mEntry.Name);
            string modelPath = Path.Combine(modelDir, ModelOnnxFileName);
            if (!File.Exists(modelPath))
                throw new FileNotFoundException(
                    $"ONNX reranker model not found at '{modelPath}'. The prewarm step should have downloaded it from '{mEntry.RepoId}/{mEntry.ModelFile}'.",
                    modelPath
                );

            var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = ParseGraphOptimizationLevel(mSettings.GraphOptimizationLevel),
                IntraOpNumThreads = mSettings.IntraOpNumThreads
            };
            OnnxExecutionProvider actualProvider = OnnxExecutionProviderConfigurator.Configure(
                sessionOptions, mSettings.ExecutionProvider, capabilities, mLogger
            );
            mSession = new InferenceSession(modelPath, sessionOptions);
            mModelHasTokenTypeIds = mSession.InputMetadata.ContainsKey(InputNameTokenTypeIds);

            var loaded = LoadTokenizer(mEntry, modelDir);
            mBertTokenizer = loaded.Bert;
            mSpTokenizer = loaded.Sp;
            mPadTokenId = ResolvePadTokenId(mEntry);

            mLogger.LogInformation(
                "OnnxReRanker ready: model={Name} family={Family} batchSize={Batch} hasTokenTypeIds={HasTTI} executionProvider={Ep}",
                mEntry.Name, mEntry.TokenizerFamily, mSettings.RerankBatchSize, mModelHasTokenTypeIds, actualProvider
            );
        }
    }

    public string ModelName => mEntry?.Name ?? string.Empty;

    private readonly BertTokenizer? mBertTokenizer;
    private readonly RerankerModelEntry? mEntry;
    private readonly ILogger<OnnxReRanker> mLogger;
    private readonly bool mModelHasTokenTypeIds;
    private readonly long mPadTokenId;
    private readonly InferenceSession? mSession;
    private readonly OnnxSettings mSettings;
    private readonly SentencePieceTokenizer? mSpTokenizer;

    /// <inheritdoc />
    public Task<IReadOnlyList<ReRankResult>> ReRankAsync(string query,
                                                         IReadOnlyList<DocChunk> candidates,
                                                         int maxResults,
                                                         CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        ArgumentNullException.ThrowIfNull(candidates);

        IReadOnlyList<ReRankResult> result = mEntry == null || mSession == null
            ? PassThrough(candidates, maxResults)
            : ScoreAndRank(query, candidates, maxResults, ct);

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        mSession?.Dispose();
    }

    private static IReadOnlyList<ReRankResult> PassThrough(IReadOnlyList<DocChunk> candidates, int maxResults)
    {
        var list = new List<ReRankResult>(Math.Min(candidates.Count, maxResults));
        for(var i = 0; i < candidates.Count && i < maxResults; i++)
            list.Add(new ReRankResult { Chunk = candidates[i], RelevanceScore = 1.0f - (i * PassThroughScoreStep) });
        return list;
    }

    private IReadOnlyList<ReRankResult> ScoreAndRank(string query,
                                                     IReadOnlyList<DocChunk> candidates,
                                                     int maxResults,
                                                     CancellationToken ct)
    {
        IReadOnlyList<ReRankResult> result = [];

        if (candidates.Count > 0)
        {
            RerankerModelEntry entry = RequireEntry();
            InferenceSession session = RequireSession();

            long[] queryTokens = TokenizeWithoutSpecials(entry, query);
            float[] scores = new float[candidates.Count];

            int batchSize = Math.Max(val1: 1, mSettings.RerankBatchSize);
            for(var start = 0; start < candidates.Count; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                int end = Math.Min(start + batchSize, candidates.Count);
                ScoreBatch(entry, session, queryTokens, candidates, start, end, scores);
            }

            var ranked = new List<ReRankResult>(candidates.Count);
            for(var i = 0; i < candidates.Count; i++)
                ranked.Add(new ReRankResult { Chunk = candidates[i], RelevanceScore = scores[i] });

            ranked.Sort((a, b) => b.RelevanceScore.CompareTo(a.RelevanceScore));
            result = ranked.Count > maxResults ? ranked.GetRange(index: 0, maxResults) : ranked;

            mLogger.LogDebug(
                "OnnxReRanker scored {Count} candidates in {Batches} batch(es); top score={Top:F4}",
                candidates.Count, (candidates.Count + batchSize - 1) / batchSize,
                result.Count > 0 ? result[index: 0].RelevanceScore : float.NaN
            );
        }

        return result;
    }

    private RerankerModelEntry RequireEntry()
    {
        RerankerModelEntry result = mEntry
                                    ?? throw new InvalidOperationException(
                                        "OnnxReRanker active entry is null; this code path runs only when reranking is enabled."
                                    );
        return result;
    }

    private InferenceSession RequireSession()
    {
        InferenceSession result = mSession
                                  ?? throw new InvalidOperationException(
                                      "OnnxReRanker session is null; this code path runs only when reranking is enabled."
                                  );
        return result;
    }

    private void ScoreBatch(RerankerModelEntry entry,
                            InferenceSession session,
                            long[] queryTokens,
                            IReadOnlyList<DocChunk> candidates,
                            int start,
                            int end,
                            float[] scores)
    {
        int batchCount = end - start;
        var pairs = new long[batchCount][];
        var maxLen = 0;

        for(var i = 0; i < batchCount; i++)
        {
            long[] docTokens = TokenizeWithoutSpecials(entry, candidates[start + i].Content);
            long[] pair = BuildPair(entry, queryTokens, docTokens, entry.MaxSequenceLength);
            pairs[i] = pair;
            if (pair.Length > maxLen)
                maxLen = pair.Length;
        }

        long[] inputIds = new long[batchCount * maxLen];
        long[] attentionMask = new long[batchCount * maxLen];
        for(var i = 0; i < batchCount; i++)
        {
            long[] pair = pairs[i];
            int offset = i * maxLen;
            for(var t = 0; t < pair.Length; t++)
            {
                inputIds[offset + t] = pair[t];
                attentionMask[offset + t] = 1L;
            }
            for(var t = pair.Length; t < maxLen; t++)
            {
                inputIds[offset + t] = mPadTokenId;
                attentionMask[offset + t] = 0L;
            }
        }

        var inputs = new List<NamedOnnxValue>(InputCapacity)
        {
            NamedOnnxValue.CreateFromTensor(InputNameInputIds,
                                            new DenseTensor<long>(inputIds, new[] { batchCount, maxLen })
                                           ),
            NamedOnnxValue.CreateFromTensor(InputNameAttentionMask,
                                            new DenseTensor<long>(attentionMask, new[] { batchCount, maxLen })
                                           )
        };
        if (mModelHasTokenTypeIds)
            inputs.Add(NamedOnnxValue.CreateFromTensor(InputNameTokenTypeIds,
                                                       new DenseTensor<long>(new long[batchCount * maxLen],
                                                                             new[] { batchCount, maxLen }
                                                                            )
                                                      )
                      );

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(inputs);
        var logitsOut = results.First();
        Tensor<float> logits = logitsOut.AsTensor<float>();
        ReadOnlySpan<int> dims = logits.Dimensions;
        if (dims.Length != LogitsRank || dims[index: 0] != batchCount || dims[index: 1] != LogitsPerPair)
            throw new InvalidOperationException(
                $"ONNX reranker '{entry.Name}' produced unexpected output shape [{string.Join(",", dims.ToArray())}]; expected [{batchCount}, {LogitsPerPair}]."
            );

        for(var i = 0; i < batchCount; i++)
            scores[start + i] = NormalizeLogit(logits[i, 0]);
    }

    /// <summary>
    ///     Sigmoid-maps a raw cross-encoder logit into the (0, 1) range.
    ///     mxbai-rerank-base-v1 emits unbounded logits (typically -10..+10);
    ///     the (0, 1) normalization makes the score directly usable as a
    ///     <c>RankedResult.FinalScore</c> in
    ///     <c>SearchTools.ApplyRerankerOrderingAsync</c> and keeps it
    ///     commensurable with the [0, 1] hybrid score on pass-through
    ///     items. Sigmoid is monotonic, so per-batch ordering inside the
    ///     reranker is unchanged; only the scale changes. Exposed
    ///     <c>internal</c> so unit tests can lock in the (0, 1) contract
    ///     without spinning up an InferenceSession.
    /// </summary>
    internal static float NormalizeLogit(float logit)
    {
        float result = 1f / (1f + MathF.Exp(-logit));
        return result;
    }

    /// <summary>
    ///     Assembles a single <c>[CLS] query [SEP] doc [SEP]</c> token
    ///     sequence honoring <paramref name="maxLen" />. Exposed
    ///     <c>internal</c> for direct unit testing of the budget math:
    ///     when <paramref name="maxLen" /> drops below
    ///     <see cref="MinViableSequenceLength" /> the query budget
    ///     clamps to zero and the cross-encoder ends up scoring an empty
    ///     query, which would silently nuke recall in production.
    ///     <see cref="OnnxSettingsValidator" /> rejects such entries
    ///     before they reach this method; the test exists to lock in
    ///     that contract.
    /// </summary>
    internal static long[] BuildPair(RerankerModelEntry entry, long[] queryTokens, long[] docTokens, int maxLen)
    {
        int clsId = LookupSpecialToken(entry, ClsTokenName);
        int sepId = LookupSpecialToken(entry, SepTokenName);

        int overhead = SpecialTokenOverhead;
        int qBudget = Math.Min(queryTokens.Length, Math.Max(val1: 0, maxLen - overhead - MinDocTokens));
        int dBudget = Math.Min(docTokens.Length, Math.Max(val1: 0, maxLen - overhead - qBudget));

        long[] result = new long[overhead + qBudget + dBudget];
        var idx = 0;
        result[idx++] = clsId;
        for(var i = 0; i < qBudget; i++)
            result[idx++] = queryTokens[i];
        result[idx++] = sepId;
        for(var i = 0; i < dBudget; i++)
            result[idx++] = docTokens[i];
        result[idx] = sepId;
        return result;
    }

    private long[] TokenizeWithoutSpecials(RerankerModelEntry entry, string text)
    {
        long[] result = entry.TokenizerFamily switch
        {
            TokenizerFamily.Bert => TokenizeWithBert(entry, text),
            TokenizerFamily.SentencePiece => TokenizeWithSp(entry, text),
            var _ => throw new InvalidOperationException(
                $"Unknown TokenizerFamily '{entry.TokenizerFamily}' for reranker '{entry.Name}'."
            )
        };
        return result;
    }

    private long[] TokenizeWithBert(RerankerModelEntry entry, string text)
    {
        BertTokenizer bert = mBertTokenizer
                             ?? throw new InvalidOperationException(
                                 $"BERT tokenizer for reranker '{entry.Name}' is not initialized."
                             );
        long[] result = bert
                        .EncodeToIds(text, addSpecialTokens: false, considerPreTokenization: true,
                                     considerNormalization: true
                                    )
                        .Select(x => (long) x)
                        .ToArray();
        return result;
    }

    private long[] TokenizeWithSp(RerankerModelEntry entry, string text)
    {
        SentencePieceTokenizer sp = mSpTokenizer
                                    ?? throw new InvalidOperationException(
                                        $"SentencePiece tokenizer for reranker '{entry.Name}' is not initialized."
                                    );
        long[] result = sp.EncodeToIds(text).Select(x => (long) x).ToArray();
        return result;
    }

    private static int LookupSpecialToken(RerankerModelEntry entry, string name)
    {
        if (!entry.SpecialTokens.TryGetValue(name, out int id))
            throw new InvalidOperationException(
                $"Reranker '{entry.Name}' is missing SpecialTokens['{name}']. Add it to the entry in appsettings.json."
            );
        return id;
    }

    private static long ResolvePadTokenId(RerankerModelEntry entry)
    {
        long result = entry.SpecialTokens.TryGetValue(PadTokenName, out int padId) ? padId : DefaultPadTokenId;
        return result;
    }

    private static (BertTokenizer? Bert, SentencePieceTokenizer? Sp) LoadTokenizer(RerankerModelEntry entry,
                                                                                    string modelDir)
    {
        (BertTokenizer? Bert, SentencePieceTokenizer? Sp) result = entry.TokenizerFamily switch
        {
            TokenizerFamily.Bert => (LoadBert(entry, modelDir), null),
            TokenizerFamily.SentencePiece => (null, LoadSentencePiece(entry, modelDir)),
            var _ => throw new InvalidOperationException(
                $"Unknown TokenizerFamily '{entry.TokenizerFamily}' for reranker '{entry.Name}'."
            )
        };
        return result;
    }

    private static BertTokenizer LoadBert(RerankerModelEntry entry, string modelDir)
    {
        string vocabPath = Path.Combine(modelDir, entry.VocabFile);
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException(
                $"BERT vocab file not found at '{vocabPath}' for reranker '{entry.Name}'.",
                vocabPath
            );
        return BertTokenizer.Create(vocabPath);
    }

    private static SentencePieceTokenizer LoadSentencePiece(RerankerModelEntry entry, string modelDir)
    {
        string spmPath = Path.Combine(modelDir, entry.SpmFile);
        if (!File.Exists(spmPath))
            throw new FileNotFoundException(
                $"SentencePiece model file not found at '{spmPath}' for reranker '{entry.Name}'.",
                spmPath
            );
        using var stream = File.OpenRead(spmPath);
        var specials = new Dictionary<string, int>(entry.SpecialTokens);
        return SentencePieceTokenizer.Create(stream,
                                             addBeginningOfSentence: false,
                                             addEndOfSentence: false,
                                             specials
                                            );
    }

    private static GraphOptimizationLevel ParseGraphOptimizationLevel(OnnxGraphOptimizationLevel level)
    {
        GraphOptimizationLevel result = level switch
        {
            OnnxGraphOptimizationLevel.Disable => GraphOptimizationLevel.ORT_DISABLE_ALL,
            OnnxGraphOptimizationLevel.Basic => GraphOptimizationLevel.ORT_ENABLE_BASIC,
            OnnxGraphOptimizationLevel.Extended => GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
            OnnxGraphOptimizationLevel.All => GraphOptimizationLevel.ORT_ENABLE_ALL,
            var _ => throw new InvalidOperationException(
                $"Unhandled OnnxGraphOptimizationLevel '{level}'. Add a case to ParseGraphOptimizationLevel."
            )
        };
        return result;
    }

    private const string InputNameInputIds = "input_ids";
    private const string InputNameAttentionMask = "attention_mask";
    private const string InputNameTokenTypeIds = "token_type_ids";
    private const string ModelOnnxFileName = "model.onnx";
    private const string ClsTokenName = "[CLS]";
    private const string SepTokenName = "[SEP]";
    private const string PadTokenName = "[PAD]";
    private const int InputCapacity = 3;
    internal const int SpecialTokenOverhead = 3;
    internal const int MinDocTokens = 4;

    /// <summary>
    ///     Minimum <c>MaxSequenceLength</c> that still leaves room for at
    ///     least one query token after subtracting the [CLS]/[SEP]/[SEP]
    ///     overhead and the reserved doc-side floor. Anything below this
    ///     would clamp the query budget to zero and silently score
    ///     "empty query vs doc."
    /// </summary>
    internal const int MinViableSequenceLength = SpecialTokenOverhead + MinDocTokens + 1;
    private const int LogitsRank = 2;
    private const int LogitsPerPair = 1;
    private const long DefaultPadTokenId = 0L;
    private const float PassThroughScoreStep = 0.01f;
}
