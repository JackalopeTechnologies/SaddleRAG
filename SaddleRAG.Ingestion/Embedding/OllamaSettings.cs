// OllamaSettings.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Configuration settings for the Ollama integration.
/// </summary>
public class OllamaSettings
{
    /// <summary>
    ///     Ollama API endpoint.
    /// </summary>
    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>
    ///     Model name for embeddings.
    /// </summary>
    public string EmbeddingModel { get; set; } = DefaultEmbeddingModel;

    /// <summary>
    ///     Output dimensionality of the embedding model.
    /// </summary>
    public int EmbeddingDimensions { get; set; } = DefaultEmbeddingDimensions;

    /// <summary>
    ///     Model name for classification/chat tasks.
    /// </summary>
    public string ClassificationModel { get; set; } = DefaultClassificationModel;

    /// <summary>
    ///     Model name used by the CLI's recon fallback when no calling LLM is
    ///     available. A larger model than the classification one is preferred
    ///     because recon does broader reasoning ("what language is this",
    ///     "what's the casing convention"). The CLI refuses to silently
    ///     fall back to a smaller model when this one is not pulled.
    /// </summary>
    public string ReconModel { get; set; } = DefaultReconModel;

    /// <summary>
    ///     Minimum self-reported confidence required before a recon-produced
    ///     profile is persisted to MongoDB. Below this threshold, the CLI
    ///     refuses to write the profile unless the user explicitly accepts
    ///     a low-confidence result. Protects CI environments (which typically
    ///     lack the VRAM for the recon model) from caching bad profiles that
    ///     then drive every subsequent extraction.
    /// </summary>
    public float ReconMinConfidence { get; set; } = DefaultReconMinConfidence;

    /// <summary>
    ///     Timeout in seconds for pulling a model.
    /// </summary>
    public int ModelPullTimeoutSeconds { get; set; } = DefaultModelPullTimeoutSeconds;

    /// <summary>
    ///     Timeout in seconds for warming a generate-capable model.
    ///     Warmup should fail fast enough that startup is not blocked for minutes
    ///     when Ollama is unhealthy or a model load stalls.
    /// </summary>
    public int WarmModelTimeoutSeconds { get; set; } = DefaultWarmModelTimeoutSeconds;

    /// <summary>
    ///     Configuration section name in appsettings.
    /// </summary>
    public const string SectionName = "Ollama";

    public const string DefaultEndpoint = "http://localhost:11434";
    public const string DefaultEmbeddingModel = "nomic-embed-text";
    public const string DefaultClassificationModel = "phi4-mini:3.8b";
    public const string DefaultReconModel = "phi4:14b";
    public const int DefaultEmbeddingDimensions = 768;
    public const int DefaultModelPullTimeoutSeconds = 600;
    public const int DefaultWarmModelTimeoutSeconds = 30;
    public const float DefaultReconMinConfidence = 0.6f;
}
