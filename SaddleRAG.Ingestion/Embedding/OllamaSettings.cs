// OllamaSettings.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Configuration settings for the Ollama integration. Mirrors the
///     config-driven registry pattern used by <see cref="OnnxSettings" />:
///     classification and recon each have an ordered list of model entries
///     and an Active selector. Missing/empty selector falls back to the
///     first entry in the list.
/// </summary>
public class OllamaSettings
{
    /// <summary>
    ///     Ollama API endpoint.
    /// </summary>
    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>Optional absolute path to the single existing Ollama executable to launch.</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>Shared model directory passed to a launched server; empty inherits OLLAMA_MODELS.</summary>
    public string ModelsDirectory { get; set; } = string.Empty;

    /// <summary>Explicit opt-in to installing Ollama when no installation can be found.</summary>
    public bool AllowAutomaticInstallation { get; set; }

    /// <summary>
    ///     Model name for embeddings (used when <c>Onnx.EmbeddingEnabled</c>
    ///     is false). Kept as a single field rather than a registry because
    ///     embedding model swaps require a coordinated reembed_library run
    ///     anyway — there is no scenario where you want to flip embeddings
    ///     at runtime via a registry selector.
    /// </summary>
    public string EmbeddingModel { get; set; } = DefaultEmbeddingModel;

    /// <summary>
    ///     Output dimensionality of the embedding model.
    /// </summary>
    public int EmbeddingDimensions { get; set; } = DefaultEmbeddingDimensions;

    /// <summary>
    ///     Apply model-specific document/query instructions. Enable when creating
    ///     or re-embedding a library. False preserves older unprefixed vectors.
    /// </summary>
    public bool EmbeddingUseTaskPrefixes { get; set; }

    /// <summary>Maximum texts per sequential embedding request (1–64).</summary>
    public int EmbeddingBatchSize { get; set; } = DefaultEmbeddingBatchSize;

    /// <summary>
    ///     Embedding-only idle retention (0–300 seconds). Zero unloads after each
    ///     request. Classification and recon retention are unaffected.
    /// </summary>
    public int EmbeddingKeepAliveSeconds { get; set; } = DefaultEmbeddingKeepAliveSeconds;

    /// <summary>Bound embedding CPU threads; GPU use remains automatic.</summary>
    public int EmbeddingThreads { get; set; } = DefaultEmbeddingThreads;

    /// <summary>
    ///     Optional token capacity for both context and the internal embedding
    ///     batch. Null uses 8192 for embeddinggemma-2 and Ollama defaults otherwise.
    /// </summary>
    public int? EmbeddingContextLength { get; set; }

    /// <summary>
    ///     Name of the <see cref="ClassificationModels" /> entry to use for
    ///     symbol categorization in <c>reextract_library</c>. Missing or
    ///     empty falls back to the first entry. Invalid name throws at
    ///     startup.
    /// </summary>
    public string ActiveClassificationModel { get; set; } = string.Empty;

    /// <summary>
    ///     Name of the <see cref="ReconModels" /> entry used by the CLI
    ///     recon fallback. Missing or empty falls back to the first entry.
    /// </summary>
    public string ActiveReconModel { get; set; } = string.Empty;

    /// <summary>
    ///     Ordered registry of classification models. <strong>First entry
    ///     is the default</strong> when <see cref="ActiveClassificationModel" />
    ///     is unset.
    /// </summary>
    public List<OllamaModelEntry> ClassificationModels { get; set; } = [];

    /// <summary>
    ///     Ordered registry of recon models. <strong>First entry is the
    ///     default</strong> when <see cref="ActiveReconModel" /> is unset.
    ///     Recon does broader reasoning ("what language is this", "what's
    ///     the casing convention") than classification and typically wants
    ///     a larger model.
    /// </summary>
    public List<OllamaModelEntry> ReconModels { get; set; } = [];

    /// <summary>
    ///     Minimum self-reported confidence required before a recon-produced
    ///     profile is persisted to MongoDB. Below this threshold, the CLI
    ///     refuses to write the profile unless the user explicitly accepts
    ///     a low-confidence result.
    /// </summary>
    public float ReconMinConfidence { get; set; } = DefaultReconMinConfidence;

    /// <summary>
    ///     Timeout in seconds for pulling a model.
    /// </summary>
    public int ModelPullTimeoutSeconds { get; set; } = DefaultModelPullTimeoutSeconds;

    /// <summary>
    ///     Timeout in seconds for warming a generate-capable model. Fail
    ///     fast enough that startup isn't blocked for minutes when Ollama
    ///     is unhealthy or a model load stalls.
    /// </summary>
    public int WarmModelTimeoutSeconds { get; set; } = DefaultWarmModelTimeoutSeconds;

    /// <summary>
    ///     Resolves the active <see cref="OllamaModelEntry" /> for
    ///     classification. Throws if the registry is empty or the active
    ///     name doesn't match an entry.
    /// </summary>
    public OllamaModelEntry GetActiveClassificationModel()
    {
        var result = ResolveActive(ClassificationModels, ActiveClassificationModel,
                                   ClassificationModelsConfigPath, ActiveClassificationModelConfigPath
                                  );
        return result;
    }

    /// <summary>
    ///     Resolves the active <see cref="OllamaModelEntry" /> for recon.
    ///     Throws if the registry is empty or the active name doesn't
    ///     match an entry.
    /// </summary>
    public OllamaModelEntry GetActiveReconModel()
    {
        var result = ResolveActive(ReconModels, ActiveReconModel,
                                   ReconModelsConfigPath, ActiveReconModelConfigPath
                                  );
        return result;
    }

    private static OllamaModelEntry ResolveActive(IReadOnlyList<OllamaModelEntry> registry,
                                                   string activeName,
                                                   string registryConfigPath,
                                                   string activeConfigPath)
    {
        if (registry.Count == 0)
            throw new InvalidOperationException(
                $"{registryConfigPath} is empty; cannot resolve an active Ollama model."
            );

        OllamaModelEntry result;
        if (string.IsNullOrEmpty(activeName))
            result = registry[index: 0];
        else
            result = registry.FirstOrDefault(e => e.Name == activeName)
                     ?? throw new InvalidOperationException(
                         $"{activeConfigPath} '{activeName}' does not match any entry in {registryConfigPath}."
                     );

        return result;
    }

    /// <summary>Configuration section name in appsettings.</summary>
    public const string SectionName = "Ollama";

    public const string DefaultEndpoint = "http://localhost:11434";
    public const string DefaultEmbeddingModel = "nomic-embed-text";
    public const int DefaultEmbeddingDimensions = 768;
    public const int DefaultEmbeddingBatchSize = 16;
    public const int MaximumEmbeddingBatchSize = 64;
    public const int DefaultEmbeddingKeepAliveSeconds = 60;
    public const int MaximumEmbeddingKeepAliveSeconds = 300;
    public const int DefaultEmbeddingThreads = 4;
    public const int DefaultModelPullTimeoutSeconds = 600;
    public const int DefaultWarmModelTimeoutSeconds = 90;
    public const float DefaultReconMinConfidence = 0.6f;

    private const string ClassificationModelsConfigPath = "Ollama.ClassificationModels";
    private const string ActiveClassificationModelConfigPath = "Ollama.ActiveClassificationModel";
    private const string ReconModelsConfigPath = "Ollama.ReconModels";
    private const string ActiveReconModelConfigPath = "Ollama.ActiveReconModel";
}
