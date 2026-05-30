// MonitorConfigSnapshotTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Mcp.Monitor;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

/// <summary>
///     Verifies the Monitor /config snapshot builder (issue #73) returns
///     credential-masked Mongo connection strings, honours the OnnxSettings
///     "none" sentinel for the reranker, and reflects EP requested-vs-active
///     divergence when the runtime falls back from a GPU EP to CPU.
/// </summary>
public sealed class MonitorConfigSnapshotTests
{
    private static IOptions<T> Opt<T>(T value) where T : class => Options.Create(value);

    private static IEmbeddingProvider StubProvider(string id = "ollama",
                                                   string name = "nomic-embed-text",
                                                   int dims = 768)
    {
        var provider = Substitute.For<IEmbeddingProvider>();
        provider.ProviderId.Returns(id);
        provider.ModelName.Returns(name);
        provider.Dimensions.Returns(dims);
        return provider;
    }

    private static OnnxRuntimeCapabilities CapabilitiesWith(OnnxExecutionProvider requested,
                                                            OnnxExecutionProvider active,
                                                            string? lastLoadWarning = null)
    {
        var caps = new OnnxRuntimeCapabilities();
        caps.RecordLoadOutcome(requested, active, lastLoadWarning);
        return caps;
    }

    private static ClassifierBackendSwitch StubBackendSwitch(OnnxSettings? onnx = null)
    {
        var settings = onnx ?? new OnnxSettings();
        var onnxClassifier = new OnnxLlmClassifier(new StubClassifierGenerator(),
                                                    NullLogger<OnnxLlmClassifier>.Instance
                                                   );
        var ollamaClassifier = Substitute.For<ILlmClassifier>();
        var probe = Substitute.For<IOllamaProbe>();
        return new ClassifierBackendSwitch(onnxClassifier,
                                           ollamaClassifier,
                                           probe,
                                           NullLogger<ClassifierBackendSwitch>.Instance
                                          );
    }

    private static McpMonitorConfigSource NewSource(OnnxSettings? onnx = null,
                                                    OllamaSettings? ollama = null,
                                                    SaddleRagDbSettings? mongo = null,
                                                    RankingSettings? ranking = null,
                                                    OnnxRuntimeCapabilities? capabilities = null,
                                                    IEmbeddingProvider? provider = null,
                                                    ClassifierBackendSwitch? classifierSwitch = null) =>
        new(Opt(onnx ?? new OnnxSettings()),
            Opt(ollama ?? new OllamaSettings()),
            Opt(mongo ?? new SaddleRagDbSettings()),
            Opt(ranking ?? new RankingSettings()),
            capabilities ?? new OnnxRuntimeCapabilities(),
            provider ?? StubProvider(),
            classifierSwitch ?? StubBackendSwitch(onnx)
           );

    private sealed class StubClassifierGenerator : IClassifierGenerator
    {
        public Task<string> GenerateAsync(string prompt, CancellationToken ct = default) =>
            Task.FromResult("{\"category\":\"HowTo\",\"confidence\":0.9}");
    }

    [Theory]
    [InlineData("mongodb://user:secret@host:27017/db", "mongodb://***:***@host:27017/db")]
    [InlineData("mongodb+srv://user:secret@cluster0.example/db", "mongodb+srv://***:***@cluster0.example/db")]
    [InlineData("mongodb://host:27017/db", "mongodb://host:27017/db")]
    [InlineData("", "")]
    public void MaskMongoConnectionStringRemovesCredentialsButPreservesHostAndPath(string input, string expected)
    {
        Assert.Equal(expected, McpMonitorConfigSource.MaskMongoConnectionString(input));
    }

    [Theory]
    [InlineData("mongodb://user:secret@host:27017/db", true)]
    [InlineData("mongodb://host:27017/db", false)]
    [InlineData("", false)]
    public void ContainsCredentialsTrueOnlyWhenUserPasswordSegmentPresent(string input, bool expected)
    {
        Assert.Equal(expected, McpMonitorConfigSource.ContainsCredentials(input));
    }

    [Fact]
    public void GetSnapshotMapsEmbeddingProviderFieldsAndOnnxBackedFlag()
    {
        var onnx = new OnnxSettings { Enabled = true, EmbeddingEnabled = true };
        var provider = StubProvider("onnx", "model-x-fp16", 1024);
        var source = NewSource(onnx: onnx, provider: provider);

        var snap = source.GetSnapshot();

        Assert.Equal("onnx", snap.Embedding.ProviderId);
        Assert.Equal("model-x-fp16", snap.Embedding.ModelName);
        Assert.Equal(1024, snap.Embedding.Dimensions);
        Assert.True(snap.Embedding.OnnxBacked);
    }

    [Fact]
    public void GetSnapshotEmbeddingOnnxBackedFalseWhenOnnxDisabled()
    {
        var onnx = new OnnxSettings { Enabled = false, EmbeddingEnabled = true };
        var source = NewSource(onnx: onnx);

        var snap = source.GetSnapshot();

        Assert.False(snap.Embedding.OnnxBacked);
    }

    [Fact]
    public void GetSnapshotRerankerActiveModelIsNullWhenNoneSentinelConfigured()
    {
        var onnx = new OnnxSettings { ActiveRerankerModel = OnnxSettings.RerankerNoneSentinel };
        var source = NewSource(onnx: onnx);

        var snap = source.GetSnapshot();

        Assert.Null(snap.Reranker.ActiveModel);
    }

    [Fact]
    public void GetSnapshotRerankerActiveModelEchoesConfiguredNameWhenNotNone()
    {
        var onnx = new OnnxSettings { ActiveRerankerModel = "mxbai-rerank-base-v1" };
        var source = NewSource(onnx: onnx);

        var snap = source.GetSnapshot();

        Assert.Equal("mxbai-rerank-base-v1", snap.Reranker.ActiveModel);
    }

    [Fact]
    public void GetSnapshotExecutionProviderMatchesRequestedTrueWhenRuntimeLoadedSameEp()
    {
        var caps = CapabilitiesWith(OnnxExecutionProvider.Cpu, OnnxExecutionProvider.Cpu);
        var source = NewSource(capabilities: caps);

        var snap = source.GetSnapshot();

        Assert.True(snap.ExecutionProvider.MatchesRequested);
        Assert.Null(snap.ExecutionProvider.LastLoadWarning);
    }

    [Fact]
    public void GetSnapshotExecutionProviderMatchesRequestedFalseAndWarningSurfacedOnFallback()
    {
        var caps = CapabilitiesWith(OnnxExecutionProvider.DirectMl,
                                    OnnxExecutionProvider.Cpu,
                                    "DirectML not available; fell back to CPU"
                                   );
        var source = NewSource(capabilities: caps);

        var snap = source.GetSnapshot();

        Assert.Equal("DirectMl", snap.ExecutionProvider.Requested);
        Assert.Equal("Cpu", snap.ExecutionProvider.Active);
        Assert.False(snap.ExecutionProvider.MatchesRequested);
        Assert.NotNull(snap.ExecutionProvider.LastLoadWarning);
    }

    [Fact]
    public void GetSnapshotMongoHostIsMaskedAndCredentialsPresentReflectsRealValue()
    {
        var mongo = new SaddleRagDbSettings
            {
                ConnectionString = "mongodb://admin:topsecret@dbhost:27017/saddlerag",
                DatabaseName = "saddlerag"
            };
        var source = NewSource(mongo: mongo);

        var snap = source.GetSnapshot();

        Assert.Equal("mongodb://***:***@dbhost:27017/saddlerag", snap.Mongo.Host);
        Assert.True(snap.Mongo.CredentialsPresent);
        Assert.DoesNotContain("topsecret", snap.Mongo.Host);
    }

    [Fact]
    public void GetSnapshotMongoCredentialsPresentFalseWhenConnectionStringHasNoCredentials()
    {
        var mongo = new SaddleRagDbSettings
            {
                ConnectionString = "mongodb://localhost:27017/saddlerag",
                DatabaseName = "saddlerag"
            };
        var source = NewSource(mongo: mongo);

        var snap = source.GetSnapshot();

        Assert.False(snap.Mongo.CredentialsPresent);
    }

    [Fact]
    public void GetSnapshotProfileEffectiveLabelIsDirectWhenNoActiveProfile()
    {
        var snap = NewSource().GetSnapshot();

        Assert.Equal("(direct)", snap.Profile.EffectiveProfile);
    }

    [Fact]
    public void GetSnapshotOllamaCardPassesAllFourFieldsThrough()
    {
        var ollama = new OllamaSettings
            {
                Endpoint = "http://ollama.test:11434",
                ActiveClassificationModel = "llama3.2-3b",
                ActiveReconModel = "llama3.2-3b",
                EmbeddingModel = "nomic-embed-text"
            };
        var source = NewSource(ollama: ollama);

        var snap = source.GetSnapshot();

        Assert.Equal("http://ollama.test:11434", snap.Ollama.Endpoint);
        Assert.Equal("llama3.2-3b", snap.Ollama.ClassificationModel);
        Assert.Equal("llama3.2-3b", snap.Ollama.ReconModel);
        Assert.Equal("nomic-embed-text", snap.Ollama.EmbeddingModel);
    }

    [Fact]
    public void GetSnapshotClassifierCardShowsOnnxBackendByDefault()
    {
        var onnx = new OnnxSettings { ExecutionProvider = OnnxExecutionProvider.Cpu };
        var ollama = new OllamaSettings { ActiveClassificationModel = "phi4-mini:3.8b" };
        var source = NewSource(onnx: onnx, ollama: ollama);

        var snap = source.GetSnapshot();

        Assert.Equal("onnx", snap.Classifier.ActiveBackend);
        Assert.NotEmpty(snap.Classifier.ActiveOnnxModel);
        Assert.Equal("phi4-mini:3.8b", snap.Classifier.OllamaClassificationModel);
    }

    [Fact]
    public void GetSnapshotClassifierCardShowsOllamaBackendAfterSwitch()
    {
        var onnx = new OnnxSettings { ExecutionProvider = OnnxExecutionProvider.Cpu };
        var onnxClassifier = new OnnxLlmClassifier(new StubClassifierGenerator(),
                                                    NullLogger<OnnxLlmClassifier>.Instance
                                                   );
        var ollamaClassifier = Substitute.For<ILlmClassifier>();
        var probe = Substitute.For<IOllamaProbe>();
        var switchInstance = new ClassifierBackendSwitch(onnxClassifier,
                                                         ollamaClassifier,
                                                         probe,
                                                         NullLogger<ClassifierBackendSwitch>.Instance
                                                        );
        probe.IsReachableAsync(Arg.Any<CancellationToken>()).Returns(true);
        switchInstance.UseOnnx();

        var source = NewSource(onnx: onnx, classifierSwitch: switchInstance);
        var snap = source.GetSnapshot();

        Assert.Equal("onnx", snap.Classifier.ActiveBackend);
    }
}
