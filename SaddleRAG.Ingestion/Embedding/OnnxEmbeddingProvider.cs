// OnnxEmbeddingProvider.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

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
///     Embedding provider that runs an ONNX model in-process via
///     Microsoft.ML.OnnxRuntime. No external service, no IPC.
///     Selects the active model from <see cref="OnnxSettings.EmbeddingModels" />
///     per the first-in-list-is-default convention. Phase 1 spike confirmed
///     this path against nomic-embed-text-v1.5 fp16.
/// </summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    public OnnxEmbeddingProvider(IOptions<OnnxSettings> settings,
                                 OnnxRuntimeCapabilities capabilities,
                                 ILogger<OnnxEmbeddingProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(logger);

        mSettings = settings.Value;
        mLogger = logger;
        mEntry = mSettings.GetActiveEmbeddingModel();

        mModelsDir = ResolveModelDir(mSettings.ModelsDir, mEntry.Name);
        string modelPath = Path.Combine(mModelsDir, ModelOnnxFileName);
        string vocabPath = Path.Combine(mModelsDir, mEntry.VocabFile);

        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                $"ONNX embedding model not found at '{modelPath}'. The prewarm step should have downloaded it from '{mEntry.RepoId}/{mEntry.ModelFile}'.",
                modelPath
            );

        mTokenizer = CreateTokenizer(mEntry, mModelsDir, vocabPath);

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

        mLogger.LogInformation(
            "OnnxEmbeddingProvider ready: model={Name} dims={Dims} family={Family} hasTokenTypeIds={HasTTI} executionProvider={Ep}",
            mEntry.Name, mEntry.Dimensions, mEntry.TokenizerFamily, mModelHasTokenTypeIds, actualProvider
        );
    }

    #region IEmbeddingProvider properties

    /// <inheritdoc />
    public string ProviderId => ProviderIdValue;

    /// <inheritdoc />
    public string ModelName => mEntry.Name;

    /// <inheritdoc />
    public int Dimensions => mEntry.Dimensions;

    #endregion

    private readonly EmbeddingModelEntry mEntry;
    private readonly ILogger<OnnxEmbeddingProvider> mLogger;
    private readonly bool mModelHasTokenTypeIds;
    private readonly string mModelsDir;
    private readonly InferenceSession mSession;
    private readonly OnnxSettings mSettings;
    private readonly BertTokenizer mTokenizer;

    /// <inheritdoc />
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts,
                                            EmbedRole role = EmbedRole.Document,
                                            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        float[][] result = [];
        string prefix = SelectPrefix(role);

        if (texts.Count > 0)
        {
            result = new float[texts.Count][];
            for(var i = 0; i < texts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                result[i] = EmbedSingle(texts[i], prefix);
                if ((i + 1) % LogProgressInterval == 0)
                    mLogger.LogDebug("Embedded {Count}/{Total} texts via ONNX ({Model}) as {Role}",
                                     i + 1, texts.Count, mEntry.Name, role
                                    );
            }
            mLogger.LogInformation("Embedded {Count} texts via ONNX ({Model}) as {Role}",
                                   texts.Count, mEntry.Name, role
                                  );
        }

        return await Task.FromResult(result);
    }

    private string SelectPrefix(EmbedRole role)
    {
        string result = role switch
        {
            EmbedRole.Document => mEntry.DocumentPrefix,
            EmbedRole.Query => mEntry.QueryPrefix,
            var _ => throw new InvalidOperationException($"Unknown EmbedRole '{role}'.")
        };
        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        mSession.Dispose();
    }

    private float[] EmbedSingle(string text, string prefix)
    {
        string prefixed = string.IsNullOrEmpty(prefix) ? text : prefix + text;
        IReadOnlyList<int> tokenIds = mTokenizer.EncodeToIds(prefixed);

        int seqLen = Math.Min(tokenIds.Count, mEntry.MaxSequenceLength);
        long[] inputIds = new long[seqLen];
        long[] attentionMask = new long[seqLen];
        for(var i = 0; i < seqLen; i++)
        {
            inputIds[i] = tokenIds[i];
            attentionMask[i] = 1L;
        }

        var inputs = new List<NamedOnnxValue>(InputCapacity)
        {
            NamedOnnxValue.CreateFromTensor(InputNameInputIds, new DenseTensor<long>(inputIds, new[] { 1, seqLen })),
            NamedOnnxValue.CreateFromTensor(InputNameAttentionMask,
                                            new DenseTensor<long>(attentionMask, new[] { 1, seqLen }))
        };
        if (mModelHasTokenTypeIds)
            inputs.Add(NamedOnnxValue.CreateFromTensor(InputNameTokenTypeIds,
                                                       new DenseTensor<long>(new long[seqLen], new[] { 1, seqLen })
                                                      )
                      );

        float[] pooled;
        OnnxInferenceGate.Acquire();
        try
        {
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = mSession.Run(inputs);
            Tensor<float> hidden = results.First().AsTensor<float>();
            ValidateHiddenShape(hidden);
            pooled = PoolAndNormalize(hidden, seqLen, attentionMask);
        }
        finally
        {
            OnnxInferenceGate.Release();
        }

        return pooled;
    }

    private void ValidateHiddenShape(Tensor<float> hidden)
    {
        ReadOnlySpan<int> dims = hidden.Dimensions;
        if (dims.Length != HiddenStateDimsRank || dims[HiddenDimIndex] != mEntry.Dimensions)
            throw new InvalidOperationException(
                $"ONNX model '{mEntry.Name}' produced unexpected output shape [{string.Join(",", dims.ToArray())}]; expected [batch, seq, {mEntry.Dimensions}]."
            );
    }

    private float[] PoolAndNormalize(Tensor<float> hidden, int seqLen, long[] attentionMask)
    {
        int hiddenDim = mEntry.Dimensions;
        float[] pooled = new float[hiddenDim];
        int valid = AccumulatePooled(hidden, seqLen, attentionMask, pooled, hiddenDim);
        if (valid > 0)
            for(var d = 0; d < hiddenDim; d++)
                pooled[d] /= valid;

        double norm = 0.0;
        for(var d = 0; d < hiddenDim; d++)
            norm += (double) pooled[d] * pooled[d];
        norm = Math.Sqrt(norm);
        if (norm > 0.0)
            for(var d = 0; d < hiddenDim; d++)
                pooled[d] = (float) (pooled[d] / norm);
        return pooled;
    }

    private static int AccumulatePooled(Tensor<float> hidden,
                                        int seqLen,
                                        long[] attentionMask,
                                        float[] pooled,
                                        int hiddenDim)
    {
        var valid = 0;
        for(var t = 0; t < seqLen; t++)
        {
            if (attentionMask[t] != 0L)
            {
                valid++;
                AccumulateOneToken(hidden, t, pooled, hiddenDim);
            }
        }
        return valid;
    }

    private static void AccumulateOneToken(Tensor<float> hidden, int t, float[] pooled, int hiddenDim)
    {
        for(var d = 0; d < hiddenDim; d++)
            pooled[d] += hidden[0, t, d];
    }

    private static BertTokenizer CreateTokenizer(EmbeddingModelEntry entry, string modelsDir, string vocabPath)
    {
        BertTokenizer result = entry.TokenizerFamily switch
        {
            TokenizerFamily.Bert => CreateBertTokenizer(vocabPath, entry),
            TokenizerFamily.SentencePiece => throw new NotSupportedException(
                $"Embedding model '{entry.Name}' has TokenizerFamily=SentencePiece, but the embedding provider only supports Bert tokenization in v1. Move SentencePiece embedding models to a follow-up."
            ),
            var _ => throw new InvalidOperationException(
                $"Unknown TokenizerFamily '{entry.TokenizerFamily}' for embedding model '{entry.Name}'."
            )
        };
        _ = modelsDir;
        return result;
    }

    private static BertTokenizer CreateBertTokenizer(string vocabPath, EmbeddingModelEntry entry)
    {
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException(
                $"BERT vocab file not found at '{vocabPath}' for embedding model '{entry.Name}'. The prewarm step should have downloaded it from '{entry.RepoId}/{entry.VocabFile}'.",
                vocabPath
            );
        BertTokenizer tokenizer = BertTokenizer.Create(vocabPath);
        return tokenizer;
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

    private static string ResolveModelDir(string baseDir, string modelName)
    {
        string result = Path.Combine(baseDir, modelName);
        return result;
    }

    private const string ProviderIdValue = "onnx";
    private const string InputNameInputIds = "input_ids";
    private const string InputNameAttentionMask = "attention_mask";
    private const string InputNameTokenTypeIds = "token_type_ids";
    private const string ModelOnnxFileName = "model.onnx";
    private const int InputCapacity = 3;
    private const int LogProgressInterval = 50;
    private const int HiddenStateDimsRank = 3;
    private const int HiddenDimIndex = 2;
}
