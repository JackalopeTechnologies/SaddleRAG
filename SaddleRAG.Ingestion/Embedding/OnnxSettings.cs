// OnnxSettings.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Configuration for the in-process ONNX Runtime providers. Bound from
///     the <c>"Onnx"</c> section of appsettings.json. When <see cref="Enabled" />
///     is false the embedding provider falls back to Ollama and reranking
///     is disabled regardless of other fields.
///     The <see cref="EmbeddingModels" /> and <see cref="RerankerModels" />
///     lists are an ordered registry. First entry is the default if the
///     matching Active selector is unset. Switching the active model is one
///     of: change the selector to a name in the list, or reorder so the
///     desired entry is first.
/// </summary>
public class OnnxSettings
{
    /// <summary>
    ///     Master switch. When false, <see cref="OllamaEmbeddingProvider" />
    ///     is registered as the active <c>IEmbeddingProvider</c> and ONNX
    ///     reranking is disabled regardless of <see cref="RerankerModels" />.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     When <see cref="Enabled" /> is true, this picks whether
    ///     <c>OnnxEmbeddingProvider</c> (true) or <c>OllamaEmbeddingProvider</c>
    ///     (false) is registered as <c>IEmbeddingProvider</c>.
    /// </summary>
    public bool EmbeddingEnabled { get; set; } = false;

    /// <summary>
    ///     Name of the <see cref="EmbeddingModels" /> entry to use. Missing
    ///     or empty falls back to the first entry. An invalid name throws
    ///     at startup rather than silently defaulting (config typos are
    ///     bugs, not preferences).
    /// </summary>
    public string ActiveEmbeddingModel { get; set; } = string.Empty;

    /// <summary>
    ///     Name of the <see cref="RerankerModels" /> entry to use. Missing
    ///     or empty falls back to the first entry. The literal value
    ///     <c>"none"</c> (case-insensitive) disables reranking entirely
    ///     (returns candidates as-is). Any other value that doesn't match
    ///     an entry throws at startup.
    /// </summary>
    public string ActiveRerankerModel { get; set; } = string.Empty;

    /// <summary>
    ///     Where downloaded model files land on disk. LocalSystem-writable
    ///     so the Windows service account can populate it during the MSI
    ///     prewarm step.
    /// </summary>
    public string ModelsDir { get; set; } = DefaultModelsDir;

    /// <summary>
    ///     ONNX Runtime graph optimization level. Default <c>"Basic"</c>
    ///     maps to <c>ORT_ENABLE_BASIC</c>. <c>ORT_ENABLE_ALL</c> triggers
    ///     a <c>SimplifiedLayerNormFusion</c> bug in ORT 1.26 against the
    ///     precision-cast nodes both nomic-fp16 and mxbai exports contain.
    ///     Do not change without testing every entry in the registry.
    ///     Allowed: <c>"Disable"</c>, <c>"Basic"</c>, <c>"Extended"</c>, <c>"All"</c>.
    /// </summary>
    public string GraphOptimizationLevel { get; set; } = DefaultGraphOptimizationLevel;

    /// <summary>
    ///     ONNX Runtime intra-op thread count. 0 lets ORT pick based on
    ///     available CPU cores.
    /// </summary>
    public int IntraOpNumThreads { get; set; } = 0;

    /// <summary>
    ///     Max number of (query, doc) pairs the reranker passes through a
    ///     single ONNX inference call. Batching is what keeps default-on
    ///     rerank under ~200 ms per search. Do not set below 16 in production.
    /// </summary>
    public int RerankBatchSize { get; set; } = DefaultRerankBatchSize;

    /// <summary>
    ///     Preferred ONNX Runtime execution provider for the embedding and
    ///     reranker sessions. Accepted values: <c>"Cpu"</c> (default),
    ///     <c>"DirectMl"</c> (Windows DX12 GPU via the DirectML EP),
    ///     <c>"Cuda"</c> (NVIDIA GPU via the CUDA EP). Comparison is
    ///     case-insensitive. CPU is always available; the GPU providers
    ///     only take effect when the build was published with
    ///     <c>UseGpu=true</c> (which swaps the OnnxRuntime NuGet to the
    ///     matching GPU package). If a GPU provider is requested but the
    ///     compiled-in runtime doesn't support it, the session falls back
    ///     to CPU and a warning is logged; <see cref="OnnxRuntimeCapabilities" />
    ///     records the outcome so the <c>list_execution_providers</c> MCP
    ///     tool can report it. Changes take effect on next process start.
    /// </summary>
    public string ExecutionProvider { get; set; } = ExecutionProviderCpu;

    /// <summary>
    ///     Ordered registry of embedding model entries. <strong>First entry
    ///     is the default</strong> when <see cref="ActiveEmbeddingModel" /> is
    ///     unset.
    /// </summary>
    public List<EmbeddingModelEntry> EmbeddingModels { get; set; } = [];

    /// <summary>
    ///     Ordered registry of reranker model entries. <strong>First entry
    ///     is the default</strong> when <see cref="ActiveRerankerModel" /> is
    ///     unset. Set <see cref="ActiveRerankerModel" /> to empty/null to
    ///     disable reranking without removing entries from the list.
    /// </summary>
    public List<RerankerModelEntry> RerankerModels { get; set; } = [];

    /// <summary>Configuration section name in appsettings.</summary>
    public const string SectionName = "Onnx";

    /// <summary>
    ///     Sentinel value for <see cref="ActiveRerankerModel" /> meaning
    ///     "rerank disabled". Compared case-insensitively.
    /// </summary>
    public const string RerankerNoneSentinel = "none";

    public const string DefaultGraphOptimizationLevel = "Basic";

    public const int DefaultRerankBatchSize = 64;

    /// <summary>CPU execution provider sentinel. Always available regardless of build flavor.</summary>
    public const string ExecutionProviderCpu = "Cpu";

    /// <summary>DirectML execution provider sentinel. Only effective when built with <c>UseGpu=true</c> against the DirectML NuGet.</summary>
    public const string ExecutionProviderDirectMl = "DirectMl";

    /// <summary>CUDA execution provider sentinel. Only effective when built with <c>UseGpu=true</c> against the CUDA NuGet.</summary>
    public const string ExecutionProviderCuda = "Cuda";

    public static string DefaultModelsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     ModelsDirAppName,
                     ModelsDirModelsSegment,
                     ModelsDirOnnxSegment
                    );

    private const string ModelsDirAppName = "SaddleRAG";
    private const string ModelsDirModelsSegment = "models";
    private const string ModelsDirOnnxSegment = "onnx";

    /// <summary>
    ///     Returns true if <paramref name="value" /> matches a currently
    ///     supported execution-provider sentinel (case-insensitive). Used
    ///     by the <c>set_execution_provider</c> MCP tool to validate input
    ///     before mutating the setting. Only <see cref="ExecutionProviderCpu" />
    ///     and <see cref="ExecutionProviderDirectMl" /> are accepted today;
    ///     <see cref="ExecutionProviderCuda" /> stays a defined sentinel for
    ///     future-proofing but is rejected here because no CUDA-flavored
    ///     OnnxRuntime NuGet ships with the project — appending the CUDA
    ///     EP would always fail at session creation. Lift this restriction
    ///     when a CUDA build flavor is added to <c>SaddleRAG.Ingestion.csproj</c>.
    /// </summary>
    public static bool IsKnownExecutionProvider(string? value)
    {
        bool result = !string.IsNullOrEmpty(value)
                      && (string.Equals(value, ExecutionProviderCpu, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(value, ExecutionProviderDirectMl, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    ///     Resolves the active <see cref="EmbeddingModelEntry" /> per the
    ///     first-in-list-is-default convention. Throws if the registry is
    ///     empty or if <see cref="ActiveEmbeddingModel" /> names an entry
    ///     that doesn't exist.
    /// </summary>
    public EmbeddingModelEntry GetActiveEmbeddingModel()
    {
        if (EmbeddingModels.Count == 0)
            throw new InvalidOperationException("Onnx.EmbeddingModels registry is empty; cannot resolve an active embedding model.");

        EmbeddingModelEntry result = string.IsNullOrEmpty(ActiveEmbeddingModel)
            ? EmbeddingModels[index: 0]
            : EmbeddingModels.FirstOrDefault(e => e.Name == ActiveEmbeddingModel)
              ?? throw new InvalidOperationException(
                  $"Onnx.ActiveEmbeddingModel '{ActiveEmbeddingModel}' does not match any entry in EmbeddingModels."
              );

        return result;
    }

    /// <summary>
    ///     Resolves the active <see cref="RerankerModelEntry" />, or null if
    ///     reranking is disabled. Rules:
    ///     <list type="bullet">
    ///         <item>Missing or empty <see cref="ActiveRerankerModel" /> → first entry in the list (default).</item>
    ///         <item>Literal <c>"none"</c> (case-insensitive) → null (reranking disabled).</item>
    ///         <item>Any other value: look up that entry by name. Throws if not found.</item>
    ///         <item>If the registry is empty and no name was specified → null (no reranker to default to).</item>
    ///     </list>
    /// </summary>
    public RerankerModelEntry? GetActiveRerankerModel()
    {
        bool isUnset = string.IsNullOrWhiteSpace(ActiveRerankerModel);
        bool isExplicitlyNone = !isUnset
                                && string.Equals(ActiveRerankerModel, RerankerNoneSentinel, StringComparison.OrdinalIgnoreCase);

        RerankerModelEntry? result = (isExplicitlyNone, isUnset) switch
        {
            (true, _) => null,
            (false, true) => RerankerModels.Count > 0 ? RerankerModels[index: 0] : null,
            (false, false) => RerankerModels.FirstOrDefault(e => e.Name == ActiveRerankerModel)
                              ?? throw new InvalidOperationException(
                                  $"Onnx.ActiveRerankerModel '{ActiveRerankerModel}' does not match any entry in RerankerModels. " +
                                  $"Use '{RerankerNoneSentinel}' to disable reranking, leave empty for the default (first entry)."
                              )
        };

        return result;
    }
}
