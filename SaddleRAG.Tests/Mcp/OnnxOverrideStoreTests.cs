// OnnxOverrideStoreTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class OnnxOverrideStoreTests : IDisposable
{
    public OnnxOverrideStoreTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), $"onnx-override-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mTempDir);
        mFilePath = Path.Combine(mTempDir, OnnxOverrideStore.RuntimeOverridesFileName);
    }

    private readonly string mFilePath;
    private readonly string mTempDir;

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
            Directory.Delete(mTempDir, recursive: true);
    }

    [Fact]
    public void SetActiveEmbeddingModelMutatesSettingsAndWritesFile()
    {
        var settings = BuildSettingsWithRegistry();
        settings.ActiveEmbeddingModel = string.Empty;
        var store = BuildStore(settings);

        store.SetActiveEmbeddingModel("nomic-v2");

        Assert.Equal("nomic-v2", settings.ActiveEmbeddingModel);
        Assert.True(File.Exists(mFilePath));
        AssertOverride("ActiveEmbeddingModel", "nomic-v2");
    }

    [Fact]
    public void SetActiveRerankerModelHandlesNoneSentinel()
    {
        var settings = BuildSettingsWithRegistry();
        settings.ActiveRerankerModel = "mxbai-base";
        var store = BuildStore(settings);

        store.SetActiveRerankerModel(OnnxSettings.RerankerNoneSentinel);

        Assert.Equal(OnnxSettings.RerankerNoneSentinel, settings.ActiveRerankerModel);
        AssertOverride("ActiveRerankerModel", OnnxSettings.RerankerNoneSentinel);
    }

    [Fact]
    public void SetExecutionProviderPersistsAndMutates()
    {
        var settings = new OnnxSettings { ExecutionProvider = OnnxExecutionProvider.Cpu };
        var store = BuildStore(settings);

        store.SetExecutionProvider(OnnxExecutionProvider.DirectMl);

        Assert.Equal(OnnxExecutionProvider.DirectMl, settings.ExecutionProvider);
        AssertOverride("ExecutionProvider", OnnxSettings.ExecutionProviderDirectMl);
    }

    [Fact]
    public void RepeatedWritesPreserveOtherKeys()
    {
        var settings = BuildSettingsWithRegistry();
        var store = BuildStore(settings);

        store.SetActiveEmbeddingModel("nomic-v2");
        store.SetExecutionProvider(OnnxExecutionProvider.DirectMl);

        AssertOverride("ActiveEmbeddingModel", "nomic-v2");
        AssertOverride("ExecutionProvider", OnnxSettings.ExecutionProviderDirectMl);
    }

    [Fact]
    public void FilePathExposesAbsolutePathForTooling()
    {
        var store = BuildStore(new OnnxSettings());

        Assert.Equal(mFilePath, store.FilePath);
    }

    [Fact]
    public void SetActiveEmbeddingModelRejectsEmpty()
    {
        var store = BuildStore(new OnnxSettings());

        Assert.Throws<ArgumentException>(() => store.SetActiveEmbeddingModel(string.Empty));
    }

    [Fact]
    public void SetActiveEmbeddingModelRejectsUnknownName()
    {
        var settings = BuildSettingsWithRegistry();
        var store = BuildStore(settings);

        var ex = Assert.Throws<ArgumentException>(() => store.SetActiveEmbeddingModel("does-not-exist"));
        Assert.Contains("does-not-exist", ex.Message);
        Assert.False(File.Exists(mFilePath));
    }

    [Fact]
    public void SetActiveRerankerModelRejectsUnknownName()
    {
        var settings = BuildSettingsWithRegistry();
        var store = BuildStore(settings);

        var ex = Assert.Throws<ArgumentException>(() => store.SetActiveRerankerModel("typo"));
        Assert.Contains("typo", ex.Message);
        Assert.False(File.Exists(mFilePath));
    }

    [Fact]
    public void SetExecutionProviderRejectsCudaOnCurrentBuilds()
    {
        var store = BuildStore(new OnnxSettings());

        var ex = Assert.Throws<ArgumentException>(() => store.SetExecutionProvider(OnnxExecutionProvider.Cuda));
        Assert.Contains("Cuda", ex.Message);
        Assert.False(File.Exists(mFilePath));
    }

    [Fact]
    public void SetActiveEmbeddingModelRefusesToOverwriteCorruptOverrideFile()
    {
        File.WriteAllText(mFilePath, CorruptJson);
        var settings = BuildSettingsWithRegistry();
        var store = BuildStore(settings);

        var ex = Assert.Throws<InvalidOperationException>(() => store.SetActiveEmbeddingModel("nomic-v2"));
        Assert.Contains(mFilePath, ex.Message);
        // The corrupt file is preserved — operator can salvage manually.
        Assert.Equal(CorruptJson, File.ReadAllText(mFilePath));
    }

    [Fact]
    public void SetActiveEmbeddingModelRefusesNonObjectRootInOverrideFile()
    {
        // Override file is a valid JSON array; root must be an object for the
        // Onnx section nesting to work.
        File.WriteAllText(mFilePath, "[]");
        var settings = BuildSettingsWithRegistry();
        var store = BuildStore(settings);

        var ex = Assert.Throws<InvalidOperationException>(() => store.SetActiveEmbeddingModel("nomic-v2"));
        Assert.Contains(mFilePath, ex.Message);
    }

    [Fact]
    public void ConcurrentWritesProduceWellFormedJsonWithLastValueWinning()
    {
        var settings = BuildSettingsWithRegistry();
        var store = BuildStore(settings);

        // Hammer the store with 50 parallel writes. The internal lock should
        // serialize them so the final file is always well-formed and contains
        // a value the loop set (the exact one is racy; we just verify both
        // shape and presence).
        Parallel.For(fromInclusive: 0, ConcurrentWriteIterations, i =>
        {
            string name = i % 2 == 0 ? "nomic-v2" : "all-minilm-l6-v2";
            store.SetActiveEmbeddingModel(name);
        });

        Assert.True(File.Exists(mFilePath));
        string finalJson = File.ReadAllText(mFilePath);
        var root = JsonNode.Parse(finalJson) as JsonObject;
        Assert.NotNull(root);
        var onnx = root["Onnx"] as JsonObject;
        Assert.NotNull(onnx);
        string? finalValue = onnx["ActiveEmbeddingModel"]?.GetValue<string>();
        Assert.Contains(finalValue, new[] { "nomic-v2", "all-minilm-l6-v2" });
    }

    private OnnxOverrideStore BuildStore(OnnxSettings settings)
    {
        return new OnnxOverrideStore(mTempDir, Options.Create(settings),
                                     NullLogger<OnnxOverrideStore>.Instance
                                    );
    }

    private static OnnxSettings BuildSettingsWithRegistry()
    {
        var settings = new OnnxSettings();
        settings.EmbeddingModels.Add(new EmbeddingModelEntry { Name = "nomic-v2" });
        settings.EmbeddingModels.Add(new EmbeddingModelEntry { Name = "all-minilm-l6-v2" });
        settings.RerankerModels.Add(new RerankerModelEntry { Name = "mxbai-base" });
        return settings;
    }

    private const string CorruptJson = "{not valid json";
    private const int ConcurrentWriteIterations = 50;

    private void AssertOverride(string key, string expected)
    {
        string json = File.ReadAllText(mFilePath);
        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        var section = root["Onnx"] as JsonObject;
        Assert.NotNull(section);
        Assert.Equal(expected, section[key]?.GetValue<string>());
    }
}
