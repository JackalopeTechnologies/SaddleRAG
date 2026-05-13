// OnnxTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools surfacing the ONNX model registry and execution-provider
///     state to the LLM. Read tools (<c>list_*</c>) inspect <see cref="OnnxSettings" />
///     and <see cref="OnnxRuntimeCapabilities" />. Mutation tools
///     (<c>set_*</c>) persist via <see cref="OnnxOverrideStore" /> so the
///     change survives restart, and always return <c>RequiresRestart=true</c>
///     since the running <see cref="OnnxEmbeddingProvider" /> and
///     <see cref="OnnxReRanker" /> hold InferenceSessions built at startup.
///     <c>download_onnx_model</c> takes effect immediately — it just
///     fetches files into <see cref="OnnxSettings.ModelsDir" /> and
///     doesn't touch any running session.
/// </summary>
[McpServerToolType]
public static class OnnxTools
{
    #region list_embedding_models

    [McpServerTool(Name = "list_embedding_models")]
    [Description("List the ONNX embedding models registered in appsettings.json:Onnx.EmbeddingModels. " +
                 "Returns { Active, Models: [{ Name, Description, RepoId, ModelFile, TokenizerFamily, " +
                 "VocabFile, SpmFile, Dimensions, MaxSequenceLength, DocumentPrefix, QueryPrefix }] }. " +
                 "Active is the entry currently selected by Onnx.ActiveEmbeddingModel (or the first " +
                 "entry if unset). Use set_active_embedding_model to change the selection; that " +
                 "requires a restart and invalidates every stored vector (call reembed_library on " +
                 "each library afterward)."
                )]
    public static string ListEmbeddingModels(IOptions<OnnxSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        OnnxSettings value = settings.Value;
        string active = ResolveActiveEmbeddingName(value);
        var models = value.EmbeddingModels.Select(BuildEmbeddingModelView);
        var response = new
                           {
                               Active = active,
                               Models = models
                           };
        string result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    #endregion

    #region list_reranker_models

    [McpServerTool(Name = "list_reranker_models")]
    [Description("List the ONNX reranker models registered in appsettings.json:Onnx.RerankerModels. " +
                 "Returns { Active, Models: [{ Name, Description, RepoId, ModelFile, TokenizerFamily, " +
                 "VocabFile, SpmFile, MaxSequenceLength }] }. Active is the entry currently selected " +
                 "by Onnx.ActiveRerankerModel (or 'none' if reranking is disabled, or the first " +
                 "entry if unset). Use set_active_reranker_model to change the selection; that " +
                 "requires a restart."
                )]
    public static string ListRerankerModels(IOptions<OnnxSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        OnnxSettings value = settings.Value;
        string active = ResolveActiveRerankerName(value);
        var models = value.RerankerModels.Select(BuildRerankerModelView);
        var response = new
                           {
                               Active = active,
                               Models = models
                           };
        string result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    #endregion

    #region list_execution_providers

    [McpServerTool(Name = "list_execution_providers")]
    [Description("Report which ONNX Runtime execution providers (EPs) this build can use and which " +
                 "one the running embedding / reranker sessions actually loaded with. Returns " +
                 "{ CompiledIn: [...], ActiveSetting, ActiveProvider, RequestedProvider, " +
                 "LastLoadWarning }. CompiledIn is what's available in the linked OnnxRuntime " +
                 "native binaries — CPU is always present; DirectMl appears on GPU builds. " +
                 "ActiveSetting is the current Onnx.ExecutionProvider value (the request). " +
                 "ActiveProvider is what the session actually got (CPU if a GPU EP failed at " +
                 "session creation). LastLoadWarning surfaces the fallback reason if any. " +
                 "Use set_execution_provider to change the request; it requires a restart."
                )]
    public static string ListExecutionProviders(OnnxRuntimeCapabilities capabilities,
                                                IOptions<OnnxSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(settings);

        var response = new
                           {
                               CompiledIn = capabilities.CompiledInProviders,
                               ActiveSetting = settings.Value.ExecutionProvider,
                               capabilities.ActiveProvider,
                               capabilities.RequestedProvider,
                               capabilities.LastLoadWarning
                           };
        string result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    #endregion

    #region set_active_embedding_model

    [McpServerTool(Name = "set_active_embedding_model")]
    [Description("Switch the active ONNX embedding model. Writes to runtime-overrides.json so the " +
                 "value survives restart. Returns { ActiveEmbeddingModel, RequiresRestart, " +
                 "OverridesFile, Warning }. CRITICAL: changing the embedding model invalidates " +
                 "every stored vector in every library (different models produce incompatible " +
                 "vector spaces). After restart, call reembed_library against every library " +
                 "returned by list_libraries to repopulate vectors. The name must match a Name " +
                 "in list_embedding_models."
                )]
    public static string SetActiveEmbeddingModel(OnnxOverrideStore store,
                                                 IOptions<OnnxSettings> settings,
                                                 [Description("Model name from list_embedding_models")]
                                                 string name)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(name);

        OnnxSettings value = settings.Value;
        string? warning = null;
        bool exists = value.EmbeddingModels.Any(e => string.Equals(e.Name, name, StringComparison.Ordinal));
        if (exists)
            store.SetActiveEmbeddingModel(name);
        else
            warning = string.Format(UnknownEmbeddingWarningFormat, name);

        var response = new
                           {
                               ActiveEmbeddingModel = value.ActiveEmbeddingModel,
                               RequiresRestart = exists,
                               OverridesFile = store.FilePath,
                               Warning = warning
                           };
        string result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    #endregion

    #region set_active_reranker_model

    [McpServerTool(Name = "set_active_reranker_model")]
    [Description("Switch the active ONNX reranker model. Writes to runtime-overrides.json so the " +
                 "value survives restart. Returns { ActiveRerankerModel, RequiresRestart, " +
                 "OverridesFile, Warning }. Pass 'none' (case-insensitive) to disable reranking " +
                 "entirely. Otherwise the name must match a Name in list_reranker_models. " +
                 "Changing the reranker does NOT invalidate stored vectors — only embedding " +
                 "model swaps require reembed_library."
                )]
    public static string SetActiveRerankerModel(OnnxOverrideStore store,
                                                IOptions<OnnxSettings> settings,
                                                [Description("Model name from list_reranker_models, or 'none' to disable reranking"
                                                            )]
                                                string name)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(name);

        OnnxSettings value = settings.Value;
        bool isNone = string.Equals(name, OnnxSettings.RerankerNoneSentinel,
                                    StringComparison.OrdinalIgnoreCase
                                   );
        bool exists = isNone
                      || value.RerankerModels.Any(e => string.Equals(e.Name, name, StringComparison.Ordinal));

        string? warning = null;
        if (exists)
        {
            string canonical = isNone ? OnnxSettings.RerankerNoneSentinel : name;
            store.SetActiveRerankerModel(canonical);
        }
        else
            warning = string.Format(UnknownRerankerWarningFormat, name, OnnxSettings.RerankerNoneSentinel);

        var response = new
                           {
                               ActiveRerankerModel = value.ActiveRerankerModel,
                               RequiresRestart = exists,
                               OverridesFile = store.FilePath,
                               Warning = warning
                           };
        string result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    #endregion

    #region set_execution_provider

    [McpServerTool(Name = "set_execution_provider")]
    [Description("Set the ONNX Runtime execution provider (CPU, DirectMl, Cuda). Writes to " +
                 "runtime-overrides.json so the value survives restart. Returns " +
                 "{ ExecutionProvider, RequiresRestart, OverridesFile, Warning, CompiledIn }. " +
                 "Setting takes effect on next process start. If the requested provider isn't " +
                 "compiled into this build (check CompiledIn from list_execution_providers), " +
                 "the session falls back to CPU at startup and LastLoadWarning records why. " +
                 "GPU providers require the GPU build flavor (UseGpu=true at MSBuild time). " +
                 "CPU is always available."
                )]
    public static string SetExecutionProvider(OnnxOverrideStore store,
                                              IOptions<OnnxSettings> settings,
                                              [Description("Execution provider: Cpu or DirectMl. Cuda is reserved for future use and rejected today."
                                                          )]
                                              string provider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(provider);

        bool parsed = Enum.TryParse<OnnxExecutionProvider>(provider, ignoreCase: true, out var parsedValue)
                      && OnnxSettings.IsSupportedByBuild(parsedValue);
        string? warning = null;
        if (parsed)
            store.SetExecutionProvider(parsedValue);
        else
            warning = string.Format(UnknownExecutionProviderWarningFormat, provider,
                                    OnnxSettings.ExecutionProviderCpu,
                                    OnnxSettings.ExecutionProviderDirectMl,
                                    OnnxSettings.ExecutionProviderCuda
                                   );

        var response = new
                           {
                               ExecutionProvider = settings.Value.ExecutionProvider.ToString(),
                               RequiresRestart = parsed,
                               OverridesFile = store.FilePath,
                               Warning = warning
                           };
        string result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    #endregion

    #region download_onnx_model

    [McpServerTool(Name = "download_onnx_model")]
    [Description("Download a registered ONNX model into Onnx.ModelsDir. Resolves the name against " +
                 "both Onnx.EmbeddingModels and Onnx.RerankerModels — embedding wins on a name " +
                 "collision. Takes effect immediately; no restart required. Returns " +
                 "{ Downloaded, Kind, Name, Warning }. Use after set_active_embedding_model to " +
                 "stage the new model before reembed_library, or to pre-populate a model the " +
                 "registry references but the prewarm step hasn't fetched yet. Files already " +
                 "present are skipped."
                )]
    public static async Task<string> DownloadOnnxModel(OnnxModelDownloader downloader,
                                                       IOptions<OnnxSettings> settings,
                                                       [Description("Model name from list_embedding_models or list_reranker_models"
                                                                   )]
                                                       string name,
                                                       CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(name);

        OnnxSettings value = settings.Value;
        EmbeddingModelEntry? embedding = value.EmbeddingModels
                                              .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
        RerankerModelEntry? reranker = embedding == null
                                           ? value.RerankerModels
                                                  .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal))
                                           : null;

        string kind = DownloadKindNone;
        bool downloaded = false;
        string? warning = null;

        if (embedding != null)
        {
            await downloader.EnsureEmbeddingModelAsync(embedding, ct);
            kind = DownloadKindEmbedding;
            downloaded = true;
        }

        if (!downloaded && reranker != null)
        {
            await downloader.EnsureRerankerModelAsync(reranker, ct);
            kind = DownloadKindReranker;
            downloaded = true;
        }

        if (!downloaded)
            warning = string.Format(UnknownDownloadNameWarningFormat, name);

        var response = new
                           {
                               Downloaded = downloaded,
                               Kind = kind,
                               Name = name,
                               Warning = warning
                           };
        string result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    #endregion

    private static string ResolveActiveEmbeddingName(OnnxSettings settings)
    {
        string result = string.IsNullOrEmpty(settings.ActiveEmbeddingModel)
                            ? settings.EmbeddingModels.FirstOrDefault()?.Name ?? string.Empty
                            : settings.ActiveEmbeddingModel;
        return result;
    }

    private static string ResolveActiveRerankerName(OnnxSettings settings)
    {
        bool isExplicitNone = !string.IsNullOrEmpty(settings.ActiveRerankerModel)
                              && string.Equals(settings.ActiveRerankerModel,
                                               OnnxSettings.RerankerNoneSentinel,
                                               StringComparison.OrdinalIgnoreCase
                                              );
        string result = isExplicitNone
                            ? OnnxSettings.RerankerNoneSentinel
                            : string.IsNullOrEmpty(settings.ActiveRerankerModel)
                                ? settings.RerankerModels.FirstOrDefault()?.Name ?? string.Empty
                                : settings.ActiveRerankerModel;
        return result;
    }

    private static object BuildEmbeddingModelView(EmbeddingModelEntry entry)
    {
        return new
                   {
                       entry.Name,
                       entry.Description,
                       entry.RepoId,
                       entry.ModelFile,
                       TokenizerFamily = entry.TokenizerFamily.ToString(),
                       entry.VocabFile,
                       entry.SpmFile,
                       entry.Dimensions,
                       entry.MaxSequenceLength,
                       entry.DocumentPrefix,
                       entry.QueryPrefix
                   };
    }

    private static object BuildRerankerModelView(RerankerModelEntry entry)
    {
        return new
                   {
                       entry.Name,
                       entry.Description,
                       entry.RepoId,
                       entry.ModelFile,
                       TokenizerFamily = entry.TokenizerFamily.ToString(),
                       entry.VocabFile,
                       entry.SpmFile,
                       entry.MaxSequenceLength
                   };
    }

    private const string UnknownEmbeddingWarningFormat =
        "Unknown embedding model '{0}'. Call list_embedding_models for valid Names. Active model unchanged.";

    private const string UnknownRerankerWarningFormat =
        "Unknown reranker model '{0}'. Call list_reranker_models for valid Names, or pass '{1}' to disable reranking. Active reranker unchanged.";

    private const string UnknownExecutionProviderWarningFormat =
        "Unknown ExecutionProvider '{0}'. Valid values: {1}, {2}, {3}. ExecutionProvider unchanged.";

    private const string UnknownDownloadNameWarningFormat =
        "Unknown model name '{0}'. Call list_embedding_models or list_reranker_models for valid Names. No download performed.";

    private const string DownloadKindEmbedding = "Embedding";
    private const string DownloadKindReranker = "Reranker";
    private const string DownloadKindNone = "None";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions
                                                                  {
                                                                      WriteIndented = true,
                                                                      Converters = { new JsonStringEnumConverter() }
                                                                  };
}
