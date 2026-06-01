// OnnxOverrideStoreTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Collections.Concurrent;
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
    public void WriteAtomicCleansUpOrphanTmpFileWhenMoveFails()
    {
        // Force File.Move(tmpPath, mFilePath, overwrite:true) to throw by
        // holding an exclusive FileStream on mFilePath while a Set* call
        // runs. The Round 2 fix in bd9b412 wraps WriteAtomic's body in
        // try/finally + DeleteIfExists so the .tmp produced by
        // File.WriteAllText doesn't outlive the failed Move. Without that
        // cleanup the .tmp would accumulate in the content root on every
        // failed write.
        var settings = BuildSettingsWithRegistry();
        var store = BuildStore(settings);

        // Seed the override file so File.Move has something to overwrite.
        store.SetActiveEmbeddingModel("nomic-v2");

        using var lockHandle = new FileStream(mFilePath, FileMode.Open, FileAccess.Read,
                                              FileShare.None
                                             );

        var ex = Assert.ThrowsAny<IOException>(() => store.SetActiveEmbeddingModel("all-minilm-l6-v2"));
        Assert.NotNull(ex);

        // The tmp file produced by File.WriteAllText must not survive the
        // failed Move.
        string tmpPath = mFilePath + ".tmp";
        Assert.False(File.Exists(tmpPath));
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
    public void ConcurrentWritesProduceWellFormedJsonAndNeverThrow()
    {
        var settings = BuildSettingsWithRegistry();
        var store = BuildStore(settings);

        // Hammer the store with parallel writes that alternate between two
        // different keys (ActiveEmbeddingModel + ExecutionProvider). Hitting
        // the same key with two values would let a dropped-lock regression
        // still produce well-formed JSON because the second writer's content
        // overwrites the first's cleanly; alternating keys makes a torn
        // read-modify-write observable as a missing key in the final file.
        //
        // We also capture every iteration's exceptions into a ConcurrentBag —
        // without the lock, simultaneous File.Move calls on the same .tmp
        // path race and surface as IOException("file in use"). The original
        // test would have masked those as Parallel.For aggregate failure
        // with no clear "the lock was dropped" diagnostic.
        var failures = new ConcurrentBag<Exception>();
        Parallel.For(fromInclusive: 0, ConcurrentWriteIterations, i =>
        {
            try
            {
                if (i % 2 == 0)
                    store.SetActiveEmbeddingModel(i % 4 == 0 ? "nomic-v2" : "all-minilm-l6-v2");
                else
                    store.SetExecutionProvider(i % 4 == 1 ? OnnxExecutionProvider.Cpu : OnnxExecutionProvider.DirectMl);
            }
            catch(Exception ex)
            {
                failures.Add(ex);
            }
        });

        Assert.Empty(failures);
        Assert.True(File.Exists(mFilePath));
        string finalJson = File.ReadAllText(mFilePath);
        var root = JsonNode.Parse(finalJson) as JsonObject;
        Assert.NotNull(root);
        var onnx = root["Onnx"] as JsonObject;
        Assert.NotNull(onnx);

        // Both keys must be present in the final file — proves no torn
        // read-modify-write erased the other key's most-recent value.
        Assert.NotNull(onnx["ActiveEmbeddingModel"]);
        Assert.NotNull(onnx["ExecutionProvider"]);

        string? finalEmbedding = onnx["ActiveEmbeddingModel"]?.GetValue<string>();
        Assert.Contains(finalEmbedding, new[] { "nomic-v2", "all-minilm-l6-v2" });

        string? finalProvider = onnx["ExecutionProvider"]?.GetValue<string>();
        Assert.Contains(finalProvider, new[] { OnnxSettings.ExecutionProviderCpu, OnnxSettings.ExecutionProviderDirectMl });
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
