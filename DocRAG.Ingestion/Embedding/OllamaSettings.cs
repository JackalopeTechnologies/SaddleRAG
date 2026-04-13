// // OllamaSettings.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

namespace DocRAG.Ingestion.Embedding;

/// <summary>
///     Configuration settings for the Ollama integration.
/// </summary>
public class OllamaSettings
{
    /// <summary>
    ///     Ollama API endpoint.
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    ///     Model name for embeddings.
    /// </summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>
    ///     Output dimensionality of the embedding model.
    /// </summary>
    public int EmbeddingDimensions { get; set; } = DefaultEmbeddingDimensions;

    /// <summary>
    ///     Model name for classification/chat tasks.
    /// </summary>
    public string ClassificationModel { get; set; } = "qwen3:1.7b";

    /// <summary>
    ///     Model name for re-ranking search results.
    ///     Smaller instruction-following models work best here.
    /// </summary>
    public string ReRankingModel { get; set; } = "qwen3:1.7b";

    /// <summary>
    ///     Timeout in seconds for pulling a model.
    /// </summary>
    public int ModelPullTimeoutSeconds { get; set; } = DefaultModelPullTimeoutSeconds;

    /// <summary>
    ///     Whether to use Ollama-based re-ranking for search results.
    ///     When false, NoOpReRanker passes results through unchanged.
    /// </summary>
    public bool ReRankingEnabled { get; init; } = true;

    /// <summary>
    ///     Configuration section name in appsettings.
    /// </summary>
    public const string SectionName = "Ollama";

    public const int DefaultEmbeddingDimensions = 768;
    public const int DefaultModelPullTimeoutSeconds = 600;
}
