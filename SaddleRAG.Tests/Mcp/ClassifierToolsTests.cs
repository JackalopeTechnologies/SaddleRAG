// ClassifierToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class ClassifierToolsTests : IDisposable
{
    public ClassifierToolsTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), $"classifier-tools-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mTempDir);
    }

    private readonly string mTempDir;

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
            Directory.Delete(mTempDir, recursive: true);
    }

    #region get_classifier_health

    [Fact]
    public async Task GetClassifierHealthReturnsActiveBackendAndOnnxEntry()
    {
        var settings = BuildSettingsWithRegistry();
        var ollamaSettings = new OllamaSettings { ActiveClassificationModel = "phi4-mini:3.8b" };
        var backendSwitch = BuildBackendSwitch(settings);
        var probe = new StubOllamaProbe(reachable: false);

        string json = await ClassifierTools.GetClassifierHealth(
            backendSwitch, Options.Create(settings), Options.Create(ollamaSettings),
            probe, TestContext.Current.CancellationToken
        );

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        Assert.Equal("onnx", root["ActiveBackendName"]?.GetValue<string>());
        var onnxNode = root["Onnx"] as JsonObject;
        Assert.NotNull(onnxNode);
        Assert.NotNull(onnxNode["ActiveModel"]?.GetValue<string>());
        Assert.False(onnxNode["ModelFilesPresent"]?.GetValue<bool>() ?? true);
        var ollamaNode = root["Ollama"] as JsonObject;
        Assert.NotNull(ollamaNode);
        Assert.Equal("phi4-mini:3.8b", ollamaNode["ClassificationModel"]?.GetValue<string>());
        Assert.False(ollamaNode["Reachable"]?.GetValue<bool>() ?? true);
    }

    [Fact]
    public async Task GetClassifierHealthReportsTrueWhenModelFolderExists()
    {
        var settings = BuildSettingsWithRegistry();
        settings.ModelsDir = mTempDir;
        var activeEntry = ClassifierEntryResolver.Resolve(settings, settings.ExecutionProvider);
        string modelDir = Path.Combine(mTempDir, activeEntry.Name);
        Directory.CreateDirectory(modelDir);

        var backendSwitch = BuildBackendSwitch(settings);
        var probe = new StubOllamaProbe(reachable: false);

        string json = await ClassifierTools.GetClassifierHealth(
            backendSwitch, Options.Create(settings), Options.Create(new OllamaSettings()),
            probe, TestContext.Current.CancellationToken
        );

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        var onnxNode = root["Onnx"] as JsonObject;
        Assert.NotNull(onnxNode);
        Assert.True(onnxNode["ModelFilesPresent"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GetClassifierHealthReportsOllamaReachableWhenProbeReturnsTrue()
    {
        var settings = BuildSettingsWithRegistry();
        var backendSwitch = BuildBackendSwitch(settings);
        var probe = new StubOllamaProbe(reachable: true);

        string json = await ClassifierTools.GetClassifierHealth(
            backendSwitch, Options.Create(settings), Options.Create(new OllamaSettings()),
            probe, TestContext.Current.CancellationToken
        );

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        var ollamaNode = root["Ollama"] as JsonObject;
        Assert.NotNull(ollamaNode);
        Assert.True(ollamaNode["Reachable"]?.GetValue<bool>());
    }

    #endregion

    #region list_classifier_models

    [Fact]
    public void ListClassifierModelsReturnsRegistryWithActiveOnnxModel()
    {
        var settings = BuildSettingsWithRegistry();
        var backendSwitch = BuildBackendSwitch(settings);

        string json = ClassifierTools.ListClassifierModels(
            backendSwitch, Options.Create(settings), Options.Create(new OllamaSettings())
        );

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        Assert.Equal("onnx", root["ActiveBackendName"]?.GetValue<string>());
        Assert.NotNull(root["ActiveOnnxModel"]?.GetValue<string>());
        var models = root["Models"] as JsonArray;
        Assert.NotNull(models);
        Assert.Equal(expected: 3, models.Count);
        Assert.NotNull(root["OllamaOption"]);
    }

    [Fact]
    public void ListClassifierModelsReturnsEmptyArrayForEmptyRegistry()
    {
        var settings = new OnnxSettings();
        settings.ClassifierModels.Clear();
        settings.ClassifierModels.Add(new ClassifierModelEntry
                                          { Name = "dummy", RepoId = "r", ModelFolder = "f" });
        var backendSwitch = BuildBackendSwitch(settings);

        string json = ClassifierTools.ListClassifierModels(
            backendSwitch, Options.Create(settings), Options.Create(new OllamaSettings())
        );

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        var models = root["Models"] as JsonArray;
        Assert.NotNull(models);
        Assert.Single(models);
    }

    #endregion

    #region set_active_classifier_model — onnx variant

    [Fact]
    public async Task SetActiveClassifierModelOnnxVariantPersistsAndReturnsRequiresRestart()
    {
        var settings = BuildSettingsWithRegistry();
        var store = BuildStore(settings);
        var backendSwitch = BuildBackendSwitch(settings);

        string json = await ClassifierTools.SetActiveClassifierModel(
            backendSwitch, store, Options.Create(settings),
            OnnxSettings.Phi3MiniCpuName,
            TestContext.Current.CancellationToken
        );

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        Assert.Equal("onnx", root["ActiveBackend"]?.GetValue<string>());
        Assert.True(root["RequiresRestartForOnnxModelReload"]?.GetValue<bool>());
        Assert.True(root["BackendSwitchedLive"]?.GetValue<bool>());
        Assert.NotNull(root["OverridesFile"]?.GetValue<string>());
        Assert.Equal(OnnxSettings.Phi3MiniCpuName, settings.ActiveClassifierModel);
    }

    [Fact]
    public async Task SetActiveClassifierModelUnknownNameThrowsAndDoesNotMutate()
    {
        var settings = BuildSettingsWithRegistry();
        settings.ActiveClassifierModel = OnnxSettings.Phi3MiniDirectMlName;
        var store = BuildStore(settings);
        var backendSwitch = BuildBackendSwitch(settings);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            ClassifierTools.SetActiveClassifierModel(
                backendSwitch, store, Options.Create(settings),
                "does-not-exist",
                TestContext.Current.CancellationToken
            )
        );
        Assert.Contains("does-not-exist", ex.Message);
        Assert.Equal(OnnxSettings.Phi3MiniDirectMlName, settings.ActiveClassifierModel);
    }

    #endregion

    #region set_active_classifier_model — backend switching

    [Fact]
    public async Task SetActiveClassifierModelOllamaKeywordSwitchesBackendLive()
    {
        var settings = BuildSettingsWithRegistry();
        var store = BuildStore(settings);
        var backendSwitch = BuildBackendSwitch(settings, ollamaReachable: true);
        Assert.Equal("onnx", backendSwitch.ActiveBackendName);

        string json = await ClassifierTools.SetActiveClassifierModel(
            backendSwitch, store, Options.Create(settings),
            "ollama",
            TestContext.Current.CancellationToken
        );

        Assert.Equal("ollama", backendSwitch.ActiveBackendName);
        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        Assert.Equal("ollama", root["ActiveBackend"]?.GetValue<string>());
        Assert.False(root["RequiresRestartForOnnxModelReload"]?.GetValue<bool>() ?? true);
        Assert.True(root["BackendSwitchedLive"]?.GetValue<bool>());
    }

    [Fact]
    public async Task SetActiveClassifierModelOllamaKeywordThrowsWhenOllamaUnreachable()
    {
        var settings = BuildSettingsWithRegistry();
        var store = BuildStore(settings);
        var backendSwitch = BuildBackendSwitch(settings, ollamaReachable: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClassifierTools.SetActiveClassifierModel(
                backendSwitch, store, Options.Create(settings),
                "ollama",
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal("onnx", backendSwitch.ActiveBackendName);
    }

    [Fact]
    public async Task SetActiveClassifierModelOnnxKeywordSwitchesBackendToOnnxLive()
    {
        var settings = BuildSettingsWithRegistry();
        var store = BuildStore(settings);
        var backendSwitch = BuildBackendSwitch(settings, ollamaReachable: true);
        await backendSwitch.UseOllamaAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ollama", backendSwitch.ActiveBackendName);

        string json = await ClassifierTools.SetActiveClassifierModel(
            backendSwitch, store, Options.Create(settings),
            "onnx",
            TestContext.Current.CancellationToken
        );

        Assert.Equal("onnx", backendSwitch.ActiveBackendName);
        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        Assert.Equal("onnx", root["ActiveBackend"]?.GetValue<string>());
        Assert.False(root["RequiresRestartForOnnxModelReload"]?.GetValue<bool>() ?? true);
    }

    #endregion

    private OnnxOverrideStore BuildStore(OnnxSettings settings) =>
        new OnnxOverrideStore(mTempDir, Options.Create(settings),
                              NullLogger<OnnxOverrideStore>.Instance
                             );

    private static ClassifierBackendSwitch BuildBackendSwitch(OnnxSettings settings,
                                                              bool ollamaReachable = false)
    {
        var onnxClassifier = new OnnxLlmClassifier(new StubClassifierGenerator(),
                                                    NullLogger<OnnxLlmClassifier>.Instance
                                                   );
        var ollamaClassifier = new StubOllamaLlmClassifier();
        var probe = new StubOllamaProbe(ollamaReachable);
        return new ClassifierBackendSwitch(onnxClassifier,
                                           ollamaClassifier,
                                           probe,
                                           NullLogger<ClassifierBackendSwitch>.Instance
                                          );
    }

    private static OnnxSettings BuildSettingsWithRegistry()
    {
        var settings = new OnnxSettings { ExecutionProvider = OnnxExecutionProvider.Cpu };
        return settings;
    }

    private sealed class StubOllamaProbe : IOllamaProbe
    {
        public StubOllamaProbe(bool reachable)
        {
            mReachable = reachable;
        }

        private readonly bool mReachable;

        public Task<bool> IsReachableAsync(CancellationToken ct = default) =>
            Task.FromResult(mReachable);
    }

    private sealed class StubOllamaLlmClassifier : ILlmClassifier
    {
        public Task<(DocCategory Category, float Confidence)> ClassifyAsync(PageRecord page,
                                                                            string libraryHint,
                                                                            CancellationToken ct = default) =>
            Task.FromResult((DocCategory.HowTo, 0.9f));
    }

    private sealed class StubClassifierGenerator : IClassifierGenerator
    {
        public Task<string> GenerateAsync(string prompt, CancellationToken ct = default) =>
            Task.FromResult("{\"category\":\"HowTo\",\"confidence\":0.9}");
    }
}
