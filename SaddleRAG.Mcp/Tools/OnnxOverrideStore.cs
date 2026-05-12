// OnnxOverrideStore.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    /// <summary>Sets the active embedding model name and persists it.</summary>
    public void SetActiveEmbeddingModel(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        mSettings.ActiveEmbeddingModel = name;
        WriteOverride(ActiveEmbeddingModelKey, name);
    }

    /// <summary>
    ///     Sets the active reranker model name (or
    ///     <see cref="OnnxSettings.RerankerNoneSentinel" /> to disable
    ///     reranking) and persists it.
    /// </summary>
    public void SetActiveRerankerModel(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        mSettings.ActiveRerankerModel = name;
        WriteOverride(ActiveRerankerModelKey, name);
    }

    /// <summary>Sets the ONNX execution provider and persists it.</summary>
    public void SetExecutionProvider(string provider)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        mSettings.ExecutionProvider = provider;
        WriteOverride(ExecutionProviderKey, provider);
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
            result = (JsonNode.Parse(existing) as JsonObject) ?? new JsonObject();
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
        File.WriteAllText(tmpPath, serialized);
        if (File.Exists(mFilePath))
            File.Delete(mFilePath);
        File.Move(tmpPath, mFilePath);
    }

    private static string GetContentRoot(IHostEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        string result = env.ContentRootPath;
        return result;
    }

    private const string ActiveEmbeddingModelKey = "ActiveEmbeddingModel";
    private const string ActiveRerankerModelKey = "ActiveRerankerModel";
    private const string ExecutionProviderKey = "ExecutionProvider";
    private const string TempSuffix = ".tmp";

    private static readonly JsonSerializerOptions smWriteOptions = new JsonSerializerOptions { WriteIndented = true };
}
