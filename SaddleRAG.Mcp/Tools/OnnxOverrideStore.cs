// OnnxOverrideStore.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     Persists runtime mutations to <see cref="OnnxSettings" /> made via
///     the <c>set_active_embedding_model</c>, <c>set_active_reranker_model</c>,
///     and <c>set_execution_provider</c> MCP tools. Writes to a sidecar
///     <c>runtime-overrides.json</c> next to the executable so the value
///     survives the next process start, and also mutates the live
///     <see cref="OnnxSettings" /> singleton so other consumers see the
///     change immediately.
///     The InferenceSessions held by <see cref="OnnxEmbeddingProvider" /> and
///     <see cref="OnnxReRanker" /> are constructed at startup and won't pick
///     up changes mid-run — the MCP tools always return
///     <c>RequiresRestart=true</c> for that reason. Atomic .tmp + rename so
///     a crash mid-write doesn't corrupt the override file.
/// </summary>
public class OnnxOverrideStore
{
    public OnnxOverrideStore(IHostEnvironment env,
                             IOptions<OnnxSettings> settings,
                             ILogger<OnnxOverrideStore> logger)
        : this(GetContentRoot(env), settings, logger)
    {
    }

    /// <summary>
    ///     Test-friendly constructor accepting an explicit content-root path.
    ///     The production DI path resolves it from <see cref="IHostEnvironment" />.
    /// </summary>
    public OnnxOverrideStore(string contentRoot,
                             IOptions<OnnxSettings> settings,
                             ILogger<OnnxOverrideStore> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentRoot);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        mFilePath = Path.Combine(contentRoot, RuntimeOverridesFileName);
        mSettings = settings.Value;
        mLogger = logger;
    }

    /// <summary>
    ///     Absolute path of the override file. Returned to the LLM in the
    ///     MCP tool response so the user can inspect or commit the file
    ///     manually if desired.
    /// </summary>
    public string FilePath => mFilePath;

    /// <summary>
    ///     Configuration file name. Public so <c>Program.cs</c> can register
    ///     the same file as a configuration source with
    ///     <c>reloadOnChange: true</c>.
    /// </summary>
    public const string RuntimeOverridesFileName = "runtime-overrides.json";

    private readonly string mFilePath;
    private readonly ILogger<OnnxOverrideStore> mLogger;
    private readonly OnnxSettings mSettings;
    private readonly Lock mWriteLock = new();

    /// <summary>
    ///     Sets the active embedding model name and persists it. Validates
    ///     <paramref name="name" /> against <c>mSettings.EmbeddingModels</c>
    ///     before writing — the override file should never contain a name
    ///     that the registry doesn't define, because the bad value would
    ///     survive restart and break <c>OnnxSettings.GetActiveEmbeddingModel</c>
    ///     deep inside provider construction.
    /// </summary>
    public void SetActiveEmbeddingModel(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        bool exists = mSettings.EmbeddingModels.Any(e => string.Equals(e.Name, name, StringComparison.Ordinal));
        if (!exists)
            throw new ArgumentException(
                string.Format(UnknownEmbeddingNameFormat, name,
                              string.Join(",", mSettings.EmbeddingModels.Select(e => e.Name))
                             ),
                nameof(name)
            );

        mSettings.ActiveEmbeddingModel = name;
        WriteOverride(ActiveEmbeddingModelKey, name);
    }

    /// <summary>
    ///     Sets the active reranker model name (or
    ///     <see cref="OnnxSettings.RerankerNoneSentinel" /> to disable
    ///     reranking) and persists it. Validates against
    ///     <c>mSettings.RerankerModels</c> + the <c>"none"</c> sentinel.
    /// </summary>
    public void SetActiveRerankerModel(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        bool isNone = string.Equals(name, OnnxSettings.RerankerNoneSentinel, StringComparison.OrdinalIgnoreCase);
        bool exists = isNone
                      || mSettings.RerankerModels.Any(e => string.Equals(e.Name, name, StringComparison.Ordinal));
        if (!exists)
            throw new ArgumentException(
                string.Format(UnknownRerankerNameFormat, name,
                              OnnxSettings.RerankerNoneSentinel,
                              string.Join(",", mSettings.RerankerModels.Select(e => e.Name))
                             ),
                nameof(name)
            );

        string canonical = isNone ? OnnxSettings.RerankerNoneSentinel : name;
        mSettings.ActiveRerankerModel = canonical;
        WriteOverride(ActiveRerankerModelKey, canonical);
    }

    /// <summary>
    ///     Sets the active classifier model name and persists it. Validates
    ///     <paramref name="name" /> against <c>mSettings.ClassifierModels</c>
    ///     before writing — the override file should never contain a name
    ///     that the registry doesn't define, because the bad value would
    ///     survive restart and break <c>ClassifierEntryResolver.Resolve</c>.
    /// </summary>
    public void SetActiveClassifierModel(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        bool exists = mSettings.ClassifierModels.Any(e => string.Equals(e.Name, name, StringComparison.Ordinal));
        if (!exists)
            throw new ArgumentException(
                string.Format(UnknownClassifierNameFormat, name,
                              string.Join(",", mSettings.ClassifierModels.Select(e => e.Name))
                             ),
                nameof(name)
            );

        mSettings.ActiveClassifierModel = name;
        WriteOverride(ActiveClassifierModelKey, name);
    }

    /// <summary>
    ///     Sets the ONNX execution provider and persists it. Validates
    ///     against <see cref="OnnxSettings.IsSupportedByBuild" /> so a
    ///     value the build can't actually load (e.g., Cuda today) never
    ///     reaches the override file.
    /// </summary>
    public void SetExecutionProvider(OnnxExecutionProvider provider)
    {
        if (!OnnxSettings.IsSupportedByBuild(provider))
            throw new ArgumentException(
                string.Format(UnsupportedProviderFormat, provider),
                nameof(provider)
            );

        mSettings.ExecutionProvider = provider;
        WriteOverride(ExecutionProviderKey, provider.ToString());
    }

    private void WriteOverride(string key, string value)
    {
        lock (mWriteLock)
        {
            JsonObject root = LoadExistingOrEmpty();
            JsonObject onnxSection = EnsureOnnxSection(root);
            onnxSection[key] = value;
            WriteAtomic(root);
            mLogger.LogInformation("Wrote Onnx override: {Key}={Value} -> {Path}", key, value, mFilePath);
        }
    }

    private JsonObject LoadExistingOrEmpty()
    {
        JsonObject result;
        if (File.Exists(mFilePath))
        {
            string existing = File.ReadAllText(mFilePath);
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(existing);
            }
            catch(JsonException ex)
            {
                throw new InvalidOperationException(
                    string.Format(CorruptOverrideFormat, mFilePath, ex.Message)
                );
            }

            result = (parsed as JsonObject)
                     ?? throw new InvalidOperationException(
                         string.Format(NonObjectOverrideFormat, mFilePath)
                     );
        }
        else
            result = new JsonObject();
        return result;
    }

    private static JsonObject EnsureOnnxSection(JsonObject root)
    {
        JsonObject result;
        if (root[OnnxSettings.SectionName] is JsonObject existing)
            result = existing;
        else
        {
            result = new JsonObject();
            root[OnnxSettings.SectionName] = result;
        }
        return result;
    }

    private void WriteAtomic(JsonObject root)
    {
        string tmpPath = mFilePath + TempSuffix;
        string serialized = root.ToJsonString(smWriteOptions);
        try
        {
            File.WriteAllText(tmpPath, serialized);
            File.Move(tmpPath, mFilePath, overwrite: true);
        }
        finally
        {
            DeleteIfExists(tmpPath);
        }
    }

    private void DeleteIfExists(string tmpPath)
    {
        if (File.Exists(tmpPath))
        {
            // Mirrors OnnxModelDownloader.DeleteIfExists. The .tmp will
            // only still exist here if File.Move threw (file lock, AV
            // quarantine); a successful Move already consumed it.
            try
            {
                File.Delete(tmpPath);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
                                        or System.Security.SecurityException)
            {
                mLogger.LogWarning(ex, "Failed to delete temp override file {TmpPath}; an orphan may remain in the content root.", tmpPath);
            }
        }
    }

    private static string GetContentRoot(IHostEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        string result = env.ContentRootPath;
        return result;
    }

    private const string ActiveEmbeddingModelKey = "ActiveEmbeddingModel";
    private const string ActiveRerankerModelKey = "ActiveRerankerModel";
    private const string ActiveClassifierModelKey = "ActiveClassifierModel";
    private const string ExecutionProviderKey = "ExecutionProvider";
    private const string TempSuffix = ".tmp";

    private const string UnknownEmbeddingNameFormat = "Unknown embedding model '{0}'. Registry contains: [{1}]. Refusing to persist an invalid override.";
    private const string UnknownRerankerNameFormat = "Unknown reranker model '{0}'. Pass '{1}' to disable reranking, or one of the registered models: [{2}]. Refusing to persist an invalid override.";
    private const string UnknownClassifierNameFormat = "Unknown classifier model '{0}'. Registry contains: [{1}]. Refusing to persist an invalid override.";
    private const string UnsupportedProviderFormat = "ExecutionProvider '{0}' is not supported by this build. Refusing to persist an unreachable override.";
    private const string CorruptOverrideFormat = "Runtime overrides file '{0}' is not valid JSON ({1}). Refusing to overwrite — move it aside manually so any salvageable content is preserved, then retry.";
    private const string NonObjectOverrideFormat = "Runtime overrides file '{0}' parses as JSON but the root is not an object. Refusing to overwrite — move it aside manually, then retry.";

    private static readonly JsonSerializerOptions smWriteOptions = new JsonSerializerOptions { WriteIndented = true };
}
