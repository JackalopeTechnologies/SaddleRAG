// OnnxSettings.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
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
    ///     ONNX Runtime graph optimization level. Default
    ///     <see cref="OnnxGraphOptimizationLevel.Basic" /> maps to
    ///     <c>ORT_ENABLE_BASIC</c>. <see cref="OnnxGraphOptimizationLevel.All" />
    ///     triggers a <c>SimplifiedLayerNormFusion</c> bug in ORT 1.26
    ///     against the precision-cast nodes both nomic-fp16 and mxbai
    ///     exports contain — do not change without testing every entry
    ///     in the registry. Bound from a case-insensitive string in
    ///     appsettings.json (e.g. <c>"Basic"</c>); the configuration
    ///     binder rejects unknown values at startup so a typo fails
    ///     fast rather than throwing deep inside session creation.
    /// </summary>
    public OnnxGraphOptimizationLevel GraphOptimizationLevel { get; set; } = OnnxGraphOptimizationLevel.Basic;

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
    ///     reranker sessions. Bound from a case-insensitive string in
    ///     appsettings.json (e.g. <c>"DirectMl"</c>). CPU is always
    ///     available; the GPU providers only take effect when the build
    ///     was published with <c>UseGpu=true</c> (which swaps the
    ///     OnnxRuntime NuGet to the matching GPU package). If a GPU
    ///     provider is requested but the compiled-in runtime doesn't
    ///     support it, the session falls back to CPU and a warning is
    ///     logged; <see cref="OnnxRuntimeCapabilities" /> records the
    ///     outcome so the <c>list_execution_providers</c> MCP tool can
    ///     report it. Changes take effect on next process start.
    /// </summary>
    public OnnxExecutionProvider ExecutionProvider { get; set; } = OnnxExecutionProvider.Cpu;

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

    /// <summary>
    ///     Name of the <see cref="ClassifierModels" /> entry to use. Missing
    ///     or empty falls back to the first entry. An invalid name throws
    ///     at startup rather than silently defaulting.
    /// </summary>
    public string ActiveClassifierModel { get; set; } = string.Empty;

    /// <summary>
    ///     Ordered registry of classifier model entries. <strong>First entry
    ///     is the default</strong> when <see cref="ActiveClassifierModel" /> is
    ///     unset. Each entry represents one provider variant (DirectML, CUDA,
    ///     or CPU) because GenAI models ship as provider-specific folder trees
    ///     rather than a single portable <c>.onnx</c> file. Switch provider
    ///     variants by changing <see cref="ActiveClassifierModel" /> to the
    ///     desired entry name.
    ///     The list is pre-populated with the three verified
    ///     <c>microsoft/Phi-3-mini-4k-instruct-onnx</c> variants. appsettings.json
    ///     can override it entirely; the defaults are here so the server is
    ///     functional without a config entry.
    ///     Active variant is resolved to match the runtime execution provider in
    ///     DI/warmup (DirectML build -&gt; directml entry, CUDA -&gt; cuda, CPU -&gt; cpu);
    ///     registry default-to-first is the DirectML build's default — a CPU/Linux
    ///     build resolves to the cpu entry.
    /// </summary>
    // Active variant is resolved to match the runtime execution provider in DI/warmup (DirectML build -> directml entry, CUDA -> cuda, CPU -> cpu); registry default-to-first is the DirectML build's default — a CPU/Linux build resolves to the cpu entry.
    public List<ClassifierModelEntry> ClassifierModels { get; set; } =
    [
        new ClassifierModelEntry
        {
            Name = Phi3MiniDirectMlName,
            Description = "phi-3-mini-4k-instruct quantised for DirectML (int4 AWQ). Runs on any DX12 GPU — default for the DirectML build.",
            RepoId = Phi3MiniRepoId,
            ModelFolder = Phi3MiniDirectMlFolder
        },
        new ClassifierModelEntry
        {
            Name = Phi3MiniCudaName,
            Description = "phi-3-mini-4k-instruct quantised for CUDA (int4 RTN). Requires the CUDA build (UseGpuCuda=true).",
            RepoId = Phi3MiniRepoId,
            ModelFolder = Phi3MiniCudaFolder
        },
        new ClassifierModelEntry
        {
            Name = Phi3MiniCpuName,
            Description = "phi-3-mini-4k-instruct quantised for CPU (int4 RTN, acc-level-4). Use on any machine; slower than GPU variants.",
            RepoId = Phi3MiniRepoId,
            ModelFolder = Phi3MiniCpuFolder
        }
    ];

    /// <summary>Configuration section name in appsettings.</summary>
    public const string SectionName = "Onnx";

    /// <summary>
    ///     Sentinel value for <see cref="ActiveRerankerModel" /> meaning
    ///     "rerank disabled". Compared case-insensitively.
    /// </summary>
    public const string RerankerNoneSentinel = "none";

    /// <summary>
    ///     String form of the default <see cref="GraphOptimizationLevel" />
    ///     for JSON serialization in MCP tool responses. The enum's own
    ///     <see cref="OnnxGraphOptimizationLevel.Basic" /> is the source
    ///     of truth; this constant exists so callers don't have to
    ///     ToString an enum value in JSON-emitting code paths.
    /// </summary>
    public const string DefaultGraphOptimizationLevel = nameof(OnnxGraphOptimizationLevel.Basic);

    public const int DefaultRerankBatchSize = 64;

    /// <summary>
    ///     CPU execution provider sentinel string. Kept as a string constant
    ///     so callers serializing JSON for MCP tool responses and
    ///     runtime-overrides.json don't have to do their own
    ///     <see cref="OnnxExecutionProvider" /> ToString.
    /// </summary>
    public const string ExecutionProviderCpu = nameof(OnnxExecutionProvider.Cpu);

    /// <summary>
    ///     DirectML execution provider sentinel string.
    ///     See <see cref="ExecutionProviderCpu" /> for why this is kept.
    /// </summary>
    public const string ExecutionProviderDirectMl = nameof(OnnxExecutionProvider.DirectMl);

    /// <summary>
    ///     CUDA execution provider sentinel string.
    ///     See <see cref="ExecutionProviderCpu" /> for why this is kept.
    /// </summary>
    public const string ExecutionProviderCuda = nameof(OnnxExecutionProvider.Cuda);

    public static string DefaultModelsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     ModelsDirAppName,
                     ModelsDirModelsSegment,
                     ModelsDirOnnxSegment
                    );

    private const string ModelsDirAppName = "SaddleRAG";
    private const string ModelsDirModelsSegment = "models";
    private const string ModelsDirOnnxSegment = "onnx";

    /// <summary>HuggingFace repo ID for the phi-3-mini-4k-instruct ONNX variants.</summary>
    public const string Phi3MiniRepoId = "microsoft/Phi-3-mini-4k-instruct-onnx";

    /// <summary>Registry name for the phi-3-mini-4k DirectML variant entry.</summary>
    public const string Phi3MiniDirectMlName = "phi-3-mini-4k-instruct-directml";

    /// <summary>Registry name for the phi-3-mini-4k CUDA variant entry.</summary>
    public const string Phi3MiniCudaName = "phi-3-mini-4k-instruct-cuda";

    /// <summary>Registry name for the phi-3-mini-4k CPU variant entry.</summary>
    public const string Phi3MiniCpuName = "phi-3-mini-4k-instruct-cpu";

    /// <summary>
    ///     HuggingFace subfolder for the phi-3-mini-4k DirectML variant (int4 AWQ).
    ///     Verified against the live microsoft/Phi-3-mini-4k-instruct-onnx repo tree.
    /// </summary>
    public const string Phi3MiniDirectMlFolder = "directml/directml-int4-awq-block-128";

    /// <summary>
    ///     HuggingFace subfolder for the phi-3-mini-4k CUDA variant (int4 RTN).
    ///     Verified against the live microsoft/Phi-3-mini-4k-instruct-onnx repo tree.
    /// </summary>
    public const string Phi3MiniCudaFolder = "cuda/cuda-int4-rtn-block-32";

    /// <summary>
    ///     HuggingFace subfolder for the phi-3-mini-4k CPU variant (int4 RTN, acc-level-4).
    ///     Verified against the live microsoft/Phi-3-mini-4k-instruct-onnx repo tree.
    /// </summary>
    public const string Phi3MiniCpuFolder = "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4";

    /// <summary>
    ///     Returns true if <paramref name="value" /> can be parsed as a
    ///     currently-supported <see cref="OnnxExecutionProvider" />
    ///     (case-insensitive). Used by the <c>set_execution_provider</c>
    ///     MCP tool to validate the LLM's string input before mutating
    ///     the setting. Delegates to <see cref="IsSupportedByBuild" />
    ///     so valid values depend on the build flavor (CPU, DirectML, or CUDA).
    /// </summary>
    public static bool IsKnownExecutionProvider(string? value)
    {
        bool result = Enum.TryParse<OnnxExecutionProvider>(value, ignoreCase: true, out var parsed)
                      && IsSupportedByBuild(parsed);
        return result;
    }

    /// <summary>
    ///     Returns true if <paramref name="provider" /> is one the current
    ///     build can attempt to load.
    ///     <list type="bullet">
    ///         <item><see cref="OnnxExecutionProvider.Cpu" /> — always available.</item>
    ///         <item><see cref="OnnxExecutionProvider.DirectMl" /> — available in DirectML and CPU builds; will gracefully fall back to CPU on any machine lacking DX12.</item>
    ///         <item><see cref="OnnxExecutionProvider.Cuda" /> — available only in the CUDA build (<c>UseGpuCuda=true</c>, the Docker <c>:cuda</c> image).</item>
    ///     </list>
    /// </summary>
    public static bool IsSupportedByBuild(OnnxExecutionProvider provider)
    {
#if USE_GPU_CUDA
        bool result = provider switch
        {
            OnnxExecutionProvider.Cpu or OnnxExecutionProvider.Cuda => true,
            _ => false
        };
#else
        bool result = provider switch
        {
            OnnxExecutionProvider.Cpu or OnnxExecutionProvider.DirectMl => true,
            _ => false
        };
#endif
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

    /// <summary>
    ///     Resolves the active <see cref="ClassifierModelEntry" /> per the
    ///     first-in-list-is-default convention. Throws if the registry is
    ///     empty or if <see cref="ActiveClassifierModel" /> names an entry
    ///     that doesn't exist.
    /// </summary>
    public ClassifierModelEntry GetActiveClassifierModel()
    {
        if (ClassifierModels.Count == 0)
            throw new InvalidOperationException("Onnx.ClassifierModels registry is empty; cannot resolve an active classifier model.");

        ClassifierModelEntry result = string.IsNullOrEmpty(ActiveClassifierModel)
            ? ClassifierModels[index: 0]
            : ClassifierModels.FirstOrDefault(e => e.Name == ActiveClassifierModel)
              ?? throw new InvalidOperationException(
                  $"Onnx.ActiveClassifierModel '{ActiveClassifierModel}' does not match any entry in ClassifierModels."
              );

        return result;
    }
}
