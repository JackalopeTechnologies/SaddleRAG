// ClassifierTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools surfacing the classifier backend state and model registry
///     to the LLM. <c>get_classifier_health</c> is the authoritative source
///     for the active backend (ONNX or Ollama), the resolved model, and
///     whether the model files are present on disk. <c>list_classifier_models</c>
///     lists the <see cref="OnnxSettings.ClassifierModels" /> registry.
///     <c>set_active_classifier_model</c> switches between ONNX model
///     variants or switches the backend to Ollama. ONNX-model selection is
///     persisted via <see cref="OnnxOverrideStore" /> and takes effect on
///     next restart; the onnx/ollama backend switch takes effect immediately
///     (the running <see cref="ClassifierBackendSwitch" /> delegates all
///     classify calls to the newly selected backend).
/// </summary>
[McpServerToolType]
public static class ClassifierTools
{
    #region get_classifier_health

    [McpServerTool(Name = "get_classifier_health")]
    [Description("Return the live classifier backend state. Returns { ActiveBackend, Onnx: { ActiveModel, " +
                 "RepoId, ModelFolder, ModelFilesPresent }, Ollama: { Reachable, ClassificationModel, " +
                 "Endpoint } }. ActiveBackend is 'onnx' or 'ollama'. Onnx.ModelFilesPresent is true " +
                 "when the model folder exists in Onnx.ModelsDir — false means the model has not been " +
                 "downloaded yet (run the warmup or download_onnx_model). Ollama.Reachable is polled " +
                 "live. Use set_active_classifier_model to switch backends or change the ONNX variant."
                )]
    public static async Task<string> GetClassifierHealth(ClassifierBackendSwitch backendSwitch,
                                                         IOptions<OnnxSettings> settings,
                                                         IOptions<OllamaSettings> ollamaSettings,
                                                         IOllamaProbe probe,
                                                         CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(backendSwitch);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ollamaSettings);
        ArgumentNullException.ThrowIfNull(probe);

        OnnxSettings onnx = settings.Value;
        OllamaSettings ollama = ollamaSettings.Value;
        ClassifierModelEntry entry = ClassifierEntryResolver.Resolve(onnx, onnx.ExecutionProvider);

        string modelDir = Path.Combine(onnx.ModelsDir, entry.Name);
        bool filesPresent = Directory.Exists(modelDir);

        bool ollamaReachable = await probe.IsReachableAsync(ct);

        var response = new
                           {
                               backendSwitch.ActiveBackendName,
                               Onnx = new
                                          {
                                              ActiveModel = entry.Name,
                                              entry.RepoId,
                                              entry.ModelFolder,
                                              ModelFilesPresent = filesPresent
                                          },
                               Ollama = new
                                            {
                                                Reachable = ollamaReachable,
                                                ClassificationModel = ollama.ActiveClassificationModel,
                                                Endpoint = ollama.Endpoint
                                            }
                           };
        string result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    #endregion

    #region list_classifier_models

    [McpServerTool(Name = "list_classifier_models")]
    [Description("List the ONNX classifier models registered in appsettings.json:Onnx.ClassifierModels. " +
                 "Returns { ActiveBackend, ActiveOnnxModel, Models: [{ Name, Description, RepoId, " +
                 "ModelFolder }], OllamaOption: { ClassificationModel, Endpoint } }. ActiveOnnxModel " +
                 "is the entry selected by Onnx.ActiveClassifierModel (or auto-resolved from the " +
                 "execution provider if unset). Use set_active_classifier_model to change the " +
                 "selection: pass an ONNX model Name from this list, or 'ollama' to switch to the " +
                 "Ollama backend. ONNX-model changes persist via runtime-overrides.json and take " +
                 "effect on next restart; backend switches (onnx↔ollama) take effect immediately."
                )]
    public static string ListClassifierModels(ClassifierBackendSwitch backendSwitch,
                                              IOptions<OnnxSettings> settings,
                                              IOptions<OllamaSettings> ollamaSettings)
    {
        ArgumentNullException.ThrowIfNull(backendSwitch);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ollamaSettings);

        OnnxSettings onnx = settings.Value;
        OllamaSettings ollama = ollamaSettings.Value;
        ClassifierModelEntry active = ClassifierEntryResolver.Resolve(onnx, onnx.ExecutionProvider);
        var models = onnx.ClassifierModels.Select(BuildClassifierModelView);

        var response = new
                           {
                               backendSwitch.ActiveBackendName,
                               ActiveOnnxModel = active.Name,
                               Models = models,
                               OllamaOption = new
                                                  {
                                                      ClassificationModel = ollama.ActiveClassificationModel,
                                                      Endpoint = ollama.Endpoint
                                                  }
                           };
        string result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    #endregion

    #region set_active_classifier_model

    [McpServerTool(Name = "set_active_classifier_model")]
    [Description("Switch the active classifier. Pass an ONNX model Name from list_classifier_models " +
                 "to select that ONNX variant and ensure the backend is ONNX — the selection is " +
                 "persisted to runtime-overrides.json (survives restart) but the running inference " +
                 "session is NOT reloaded; a restart is required for the new ONNX model to load. " +
                 "Pass 'ollama' (case-insensitive) to switch the live backend to Ollama immediately " +
                 "— Ollama must be reachable or the call throws with an actionable message. " +
                 "Pass 'onnx' (case-insensitive) to switch back to the ONNX backend immediately " +
                 "using whatever ONNX model was last selected. Returns { ActiveBackend, " +
                 "ActiveOnnxModel, BackendSwitchedLive, RequiresRestartForOnnxModelReload, " +
                 "OverridesFile }. **Throws ArgumentException on unknown name** — report verbatim."
                )]
    public static async Task<string> SetActiveClassifierModel(
        ClassifierBackendSwitch backendSwitch,
        OnnxOverrideStore store,
        IOptions<OnnxSettings> settings,
        [Description("ONNX model name from list_classifier_models, 'onnx' to switch back to ONNX, or 'ollama' to switch to Ollama backend")]
        string name,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(backendSwitch);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(name);

        bool backendSwitchedLive = true;
        bool requiresRestartForOnnxModelReload;

        bool isOllama = string.Equals(name, ClassifierBackendNames.Ollama, StringComparison.OrdinalIgnoreCase);
        bool isOnnx = string.Equals(name, ClassifierBackendNames.Onnx, StringComparison.OrdinalIgnoreCase);

        switch (isOllama, isOnnx)
        {
            case (true, false):
                await backendSwitch.UseOllamaAsync(ct);
                requiresRestartForOnnxModelReload = false;
                break;
            case (false, true):
                backendSwitch.UseOnnx();
                requiresRestartForOnnxModelReload = false;
                break;
            default:
                store.SetActiveClassifierModel(name);
                backendSwitch.UseOnnx();
                requiresRestartForOnnxModelReload = true;
                break;
        }

        OnnxSettings onnx = settings.Value;
        ClassifierModelEntry resolved = ClassifierEntryResolver.Resolve(onnx, onnx.ExecutionProvider);

        var response = new
                           {
                               ActiveBackend = backendSwitch.ActiveBackendName,
                               ActiveOnnxModel = resolved.Name,
                               BackendSwitchedLive = backendSwitchedLive,
                               RequiresRestartForOnnxModelReload = requiresRestartForOnnxModelReload,
                               OverridesFile = isOnnx || isOllama ? null : store.FilePath
                           };
        string result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    #endregion

    private static object BuildClassifierModelView(ClassifierModelEntry entry)
    {
        return new
                   {
                       entry.Name,
                       entry.Description,
                       entry.RepoId,
                       entry.ModelFolder
                   };
    }

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions
                                                                  {
                                                                      WriteIndented = true,
                                                                      Converters = { new JsonStringEnumConverter() }
                                                                  };
}
