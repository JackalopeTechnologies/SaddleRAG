// OnnxToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class OnnxToolsTests : IDisposable
{
    public OnnxToolsTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), $"onnx-tools-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mTempDir);
    }

    private readonly string mTempDir;

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
            Directory.Delete(mTempDir, recursive: true);
    }

    #region list_embedding_models

    [Fact]
    public void ListEmbeddingModelsReturnsRegistryWithActive()
    {
        var settings = BuildSettingsWithRegistry();

        string json = OnnxTools.ListEmbeddingModels(Options.Create(settings));

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        Assert.Equal("nomic-embed-text-v1.5", root["Active"]?.GetValue<string>());
        var models = root["Models"] as JsonArray;
        Assert.NotNull(models);
        Assert.Equal(expected: 2, models.Count);
        Assert.Equal("nomic-embed-text-v1.5", models[index: 0]?["Name"]?.GetValue<string>());
        Assert.Equal(expected: 768, models[index: 0]?["Dimensions"]?.GetValue<int>());
    }

    [Fact]
    public void ListEmbeddingModelsReturnsEmptyArrayForEmptyRegistry()
    {
        var settings = new OnnxSettings();

        string json = OnnxTools.ListEmbeddingModels(Options.Create(settings));

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        var models = root["Models"] as JsonArray;
        Assert.NotNull(models);
        Assert.Empty(models);
    }

    #endregion

    #region list_reranker_models

    [Fact]
    public void ListRerankerModelsReturnsRegistryWithActiveAndNoneSentinel()
    {
        var settings = BuildSettingsWithRegistry();
        settings.ActiveRerankerModel = OnnxSettings.RerankerNoneSentinel;

        string json = OnnxTools.ListRerankerModels(Options.Create(settings));

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        Assert.Equal(OnnxSettings.RerankerNoneSentinel, root["Active"]?.GetValue<string>());
        var models = root["Models"] as JsonArray;
        Assert.NotNull(models);
        Assert.Equal(expected: 2, models.Count);
    }

    #endregion

    #region list_execution_providers

    [Fact]
    public void ListExecutionProvidersReportsCompiledInAndActiveSetting()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        var settings = new OnnxSettings { ExecutionProvider = OnnxExecutionProvider.DirectMl };

        string json = OnnxTools.ListExecutionProviders(capabilities, Options.Create(settings));

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        var compiled = root["CompiledIn"] as JsonArray;
        Assert.NotNull(compiled);
        Assert.Contains(compiled, n => n?.GetValue<string>() == OnnxSettings.ExecutionProviderCpu);

        var accepted = root["AcceptedBySettings"] as JsonArray;
        Assert.NotNull(accepted);
        var acceptedNames = accepted.Select(n => n?.GetValue<string>()).ToArray();
        Assert.Contains(OnnxSettings.ExecutionProviderCpu, acceptedNames);
        Assert.Contains(OnnxSettings.ExecutionProviderDirectMl, acceptedNames);
        Assert.DoesNotContain(OnnxSettings.ExecutionProviderCuda, acceptedNames);

        Assert.Equal(OnnxSettings.ExecutionProviderDirectMl, root["ActiveSetting"]?.GetValue<string>());
        Assert.Equal(OnnxSettings.ExecutionProviderCpu, root["ActiveProvider"]?.GetValue<string>());
    }

    #endregion

    #region set_active_embedding_model

    [Fact]
    public void SetActiveEmbeddingModelMutatesAndPersistsAndReturnsRequiresRestart()
    {
        var settings = BuildSettingsWithRegistry();
        var store = BuildStore(settings);

        string json = OnnxTools.SetActiveEmbeddingModel(store, Options.Create(settings),
                                                        "all-minilm-l6-v2"
                                                       );

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        Assert.Equal("all-minilm-l6-v2", root["ActiveEmbeddingModel"]?.GetValue<string>());
        Assert.True(root["RequiresRestart"]?.GetValue<bool>());
        Assert.Equal("all-minilm-l6-v2", settings.ActiveEmbeddingModel);
    }

    [Fact]
    public void SetActiveEmbeddingModelWithUnknownNameThrowsAndDoesNotMutate()
    {
        var settings = BuildSettingsWithRegistry();
        settings.ActiveEmbeddingModel = "nomic-embed-text-v1.5";
        var store = BuildStore(settings);

        var ex = Assert.Throws<ArgumentException>(() =>
            OnnxTools.SetActiveEmbeddingModel(store, Options.Create(settings), "does-not-exist")
        );
        Assert.Contains("does-not-exist", ex.Message);
        Assert.Equal("nomic-embed-text-v1.5", settings.ActiveEmbeddingModel);
    }

    #endregion

    #region set_active_reranker_model

    [Fact]
    public void SetActiveRerankerModelAcceptsNoneSentinel()
    {
        var settings = BuildSettingsWithRegistry();
        var store = BuildStore(settings);

        string json = OnnxTools.SetActiveRerankerModel(store, Options.Create(settings),
                                                       OnnxSettings.RerankerNoneSentinel
                                                      );

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        Assert.Equal(OnnxSettings.RerankerNoneSentinel, root["ActiveRerankerModel"]?.GetValue<string>());
        Assert.True(root["RequiresRestart"]?.GetValue<bool>());
    }

    [Fact]
    public void SetActiveRerankerModelWithUnknownNameThrows()
    {
        var settings = BuildSettingsWithRegistry();
        var store = BuildStore(settings);

        var ex = Assert.Throws<ArgumentException>(() =>
            OnnxTools.SetActiveRerankerModel(store, Options.Create(settings), "does-not-exist")
        );
        Assert.Contains("does-not-exist", ex.Message);
    }

    #endregion

    #region set_execution_provider

    [Fact]
    public void SetExecutionProviderAcceptsCanonicalNames()
    {
        var settings = new OnnxSettings();
        var store = BuildStore(settings);

        string json = OnnxTools.SetExecutionProvider(store, Options.Create(settings),
                                                     OnnxSettings.ExecutionProviderDirectMl
                                                    );

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        Assert.Equal(OnnxSettings.ExecutionProviderDirectMl, root["ExecutionProvider"]?.GetValue<string>());
        Assert.True(root["RequiresRestart"]?.GetValue<bool>());
        Assert.Equal(OnnxExecutionProvider.DirectMl, settings.ExecutionProvider);
    }

    [Fact]
    public void SetExecutionProviderRejectsUnknownProviderByThrowing()
    {
        var settings = new OnnxSettings();
        var store = BuildStore(settings);

        var ex = Assert.Throws<ArgumentException>(() =>
            OnnxTools.SetExecutionProvider(store, Options.Create(settings), "Vulkan")
        );
        Assert.Contains("Vulkan", ex.Message);
        Assert.Equal(OnnxExecutionProvider.Cpu, settings.ExecutionProvider);
    }

    #endregion

    #region download_onnx_model

    [Fact]
    public async Task DownloadOnnxModelReturnsWarningWhenNameNotInRegistry()
    {
        var settings = BuildSettingsWithRegistry();
        settings.ModelsDir = mTempDir;
        var downloader = BuildDownloader(settings);

        string json = await OnnxTools.DownloadOnnxModel(downloader, Options.Create(settings),
                                                       "missing-model", TestContext.Current.CancellationToken
                                                      );

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        Assert.NotNull(root["Warning"]?.GetValue<string>());
        Assert.False(root["Downloaded"]?.GetValue<bool>() ?? true);
    }

    [Fact]
    public async Task DownloadOnnxModelInvokesDownloaderWhenEmbeddingNameMatches()
    {
        var settings = BuildSettingsWithRegistry();
        settings.ModelsDir = mTempDir;
        var downloader = BuildDownloader(settings, "ok");

        string json = await OnnxTools.DownloadOnnxModel(downloader, Options.Create(settings),
                                                       "nomic-embed-text-v1.5",
                                                       TestContext.Current.CancellationToken
                                                      );

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        Assert.True(root["Downloaded"]?.GetValue<bool>());
        Assert.Equal("Embedding", root["Kind"]?.GetValue<string>());
    }

    #endregion

    private OnnxOverrideStore BuildStore(OnnxSettings settings)
    {
        return new OnnxOverrideStore(mTempDir, Options.Create(settings),
                                     NullLogger<OnnxOverrideStore>.Instance
                                    );
    }

    private static OnnxModelDownloader BuildDownloader(OnnxSettings settings, string body = "ok")
    {
        var handler = new StubHandler(body);
        var clientFactory = new StubHttpClientFactory(handler);
        return new OnnxModelDownloader(clientFactory, Options.Create(settings),
                                       NullLogger<OnnxModelDownloader>.Instance
                                      );
    }

    private static OnnxSettings BuildSettingsWithRegistry()
    {
        var settings = new OnnxSettings
        {
            ActiveEmbeddingModel = "nomic-embed-text-v1.5",
            ActiveRerankerModel = "mxbai-rerank-base-v1"
        };
        settings.EmbeddingModels.Add(new EmbeddingModelEntry
                                         {
                                             Name = "nomic-embed-text-v1.5",
                                             Description = "Default English embedding.",
                                             RepoId = "nomic-ai/nomic-embed-text-v1.5",
                                             ModelFile = "onnx/model_fp16.onnx",
                                             TokenizerFamily = TokenizerFamily.Bert,
                                             VocabFile = "vocab.txt",
                                             Dimensions = 768
                                         });
        settings.EmbeddingModels.Add(new EmbeddingModelEntry
                                         {
                                             Name = "all-minilm-l6-v2",
                                             Description = "Smaller symmetric embedding.",
                                             RepoId = "sentence-transformers/all-MiniLM-L6-v2",
                                             ModelFile = "onnx/model.onnx",
                                             TokenizerFamily = TokenizerFamily.Bert,
                                             VocabFile = "vocab.txt",
                                             Dimensions = 384
                                         });
        settings.RerankerModels.Add(new RerankerModelEntry
                                        {
                                            Name = "mxbai-rerank-base-v1",
                                            Description = "Default reranker.",
                                            RepoId = "mixedbread-ai/mxbai-rerank-base-v1",
                                            ModelFile = "onnx/model_quantized.onnx",
                                            TokenizerFamily = TokenizerFamily.SentencePiece,
                                            SpmFile = "spm.model"
                                        });
        settings.RerankerModels.Add(new RerankerModelEntry
                                        {
                                            Name = "mxbai-rerank-large-v1",
                                            Description = "Larger mxbai variant.",
                                            RepoId = "mixedbread-ai/mxbai-rerank-large-v1",
                                            ModelFile = "onnx/model_quantized.onnx",
                                            TokenizerFamily = TokenizerFamily.SentencePiece,
                                            SpmFile = "spm.model"
                                        });
        return settings;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public StubHandler(string body)
        {
            mBody = body;
        }

        private readonly string mBody;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
                               { Content = new StringContent(mBody) };
            return Task.FromResult(response);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            mHandler = handler;
        }

        private readonly HttpMessageHandler mHandler;

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(mHandler, disposeHandler: false);
        }
    }
}
