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
        var settings = new OnnxSettings { ActiveEmbeddingModel = string.Empty };
        var store = BuildStore(settings);

        store.SetActiveEmbeddingModel("nomic-v2");

        Assert.Equal("nomic-v2", settings.ActiveEmbeddingModel);
        Assert.True(File.Exists(mFilePath));
        AssertOverride("ActiveEmbeddingModel", "nomic-v2");
    }

    [Fact]
    public void SetActiveRerankerModelHandlesNoneSentinel()
    {
        var settings = new OnnxSettings { ActiveRerankerModel = "mxbai-base" };
        var store = BuildStore(settings);

        store.SetActiveRerankerModel(OnnxSettings.RerankerNoneSentinel);

        Assert.Equal(OnnxSettings.RerankerNoneSentinel, settings.ActiveRerankerModel);
        AssertOverride("ActiveRerankerModel", OnnxSettings.RerankerNoneSentinel);
    }

    [Fact]
    public void SetExecutionProviderPersistsAndMutates()
    {
        var settings = new OnnxSettings { ExecutionProvider = OnnxSettings.ExecutionProviderCpu };
        var store = BuildStore(settings);

        store.SetExecutionProvider(OnnxSettings.ExecutionProviderDirectMl);

        Assert.Equal(OnnxSettings.ExecutionProviderDirectMl, settings.ExecutionProvider);
        AssertOverride("ExecutionProvider", OnnxSettings.ExecutionProviderDirectMl);
    }

    [Fact]
    public void RepeatedWritesPreserveOtherKeys()
    {
        var settings = new OnnxSettings();
        var store = BuildStore(settings);

        store.SetActiveEmbeddingModel("foo");
        store.SetExecutionProvider(OnnxSettings.ExecutionProviderDirectMl);

        AssertOverride("ActiveEmbeddingModel", "foo");
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

    private OnnxOverrideStore BuildStore(OnnxSettings settings)
    {
        return new OnnxOverrideStore(mTempDir, Options.Create(settings),
                                     NullLogger<OnnxOverrideStore>.Instance
                                    );
    }

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
