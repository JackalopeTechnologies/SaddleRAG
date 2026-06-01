// OnnxModelDownloaderTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OnnxModelDownloaderTests : IDisposable
{
    public OnnxModelDownloaderTests()
    {
        mTempRoot = Path.Combine(Path.GetTempPath(), $"onnx-downloader-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mTempRoot);
    }

    private readonly string mTempRoot;

    public void Dispose()
    {
        if (Directory.Exists(mTempRoot))
            Directory.Delete(mTempRoot, recursive: true);
    }

    [Fact]
    public async Task EnsureActiveModelsAsyncWhenDisabledDoesNothing()
    {
        var handler = new RecordingHandler(_ => Make200("ignored"));
        var settings = BuildSettings(enabled: false);
        var downloader = BuildDownloader(handler, settings);

        await downloader.EnsureActiveModelsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(Path.Combine(mTempRoot, "nomic")));
    }

    [Fact]
    public async Task EnsureActiveModelsAsyncDownloadsEmbeddingModelAndVocabForBertFamily()
    {
        var handler = new RecordingHandler(req =>
        {
            string url = (req.RequestUri ?? new Uri(EmptyUrl)).ToString();
            string body = url.EndsWith(OnnxFileExtension) ? "ONNX_BYTES" : "VOCAB_BYTES";
            return Make200(body);
        });
        var settings = BuildSettings(enabled: true, embeddingEnabled: true, rerankerEntries: 0);
        var downloader = BuildDownloader(handler, settings);

        await downloader.EnsureActiveModelsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 2, handler.Requests.Count);
        string modelPath = Path.Combine(mTempRoot, "nomic", "model.onnx");
        string vocabPath = Path.Combine(mTempRoot, "nomic", "vocab.txt");
        Assert.True(File.Exists(modelPath));
        Assert.True(File.Exists(vocabPath));
        Assert.Equal("ONNX_BYTES",
                     await File.ReadAllTextAsync(modelPath, TestContext.Current.CancellationToken));
        Assert.Equal("VOCAB_BYTES",
                     await File.ReadAllTextAsync(vocabPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureActiveModelsAsyncDownloadsRerankerWithSpmForSentencePieceFamily()
    {
        var handler = new RecordingHandler(_ => Make200("BYTES"));
        var settings = BuildSettings(enabled: true, embeddingEnabled: false, rerankerEntries: 1);
        var downloader = BuildDownloader(handler, settings);

        await downloader.EnsureActiveModelsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 2, handler.Requests.Count);
        Assert.True(File.Exists(Path.Combine(mTempRoot, "mxbai", "model.onnx")));
        Assert.True(File.Exists(Path.Combine(mTempRoot, "mxbai", "spm.model")));
    }

    [Fact]
    public async Task EnsureActiveModelsAsyncSkipsDownloadWhenFileAlreadyPresent()
    {
        string nomicDir = Path.Combine(mTempRoot, "nomic");
        Directory.CreateDirectory(nomicDir);
        await File.WriteAllTextAsync(Path.Combine(nomicDir, "model.onnx"), "PRESENT",
                                     TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(nomicDir, "vocab.txt"), "PRESENT",
                                     TestContext.Current.CancellationToken);

        var handler = new RecordingHandler(_ =>
                                               throw new InvalidOperationException(
                                                   "Downloader should not have hit the network."
                                               )
                                          );
        var settings = BuildSettings(enabled: true, embeddingEnabled: true, rerankerEntries: 0);
        var downloader = BuildDownloader(handler, settings);

        await downloader.EnsureActiveModelsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.Equal("PRESENT",
                     await File.ReadAllTextAsync(Path.Combine(nomicDir, "model.onnx"),
                                                 TestContext.Current.CancellationToken
                                                ));
    }

    [Fact]
    public async Task EnsureActiveModelsAsyncRerankerNoneSentinelSkipsRerankerDownload()
    {
        var handler = new RecordingHandler(_ => Make200("BYTES"));
        var settings = BuildSettings(enabled: true, embeddingEnabled: true, rerankerEntries: 1);
        settings.ActiveRerankerModel = OnnxSettings.RerankerNoneSentinel;
        var downloader = BuildDownloader(handler, settings);

        await downloader.EnsureActiveModelsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 2, handler.Requests.Count);
        Assert.False(Directory.Exists(Path.Combine(mTempRoot, "mxbai")));
    }

    [Fact]
    public async Task EnsureEmbeddingModelAsyncThrowsWhenBertFamilyHasNoVocabFile()
    {
        var handler = new RecordingHandler(_ => Make200("BYTES"));
        var settings = new OnnxSettings { Enabled = true, EmbeddingEnabled = true, ModelsDir = mTempRoot };
        var entry = new EmbeddingModelEntry
                        {
                            Name = "broken",
                            RepoId = "test/broken",
                            ModelFile = "model.onnx",
                            TokenizerFamily = TokenizerFamily.Bert,
                            VocabFile = string.Empty,
                            Dimensions = 768
                        };
        var downloader = BuildDownloader(handler, settings);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => downloader.EnsureEmbeddingModelAsync(entry, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task DownloadFailurePropagatesAsHttpRequestExceptionAndLeavesNoMainFile()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var settings = BuildSettings(enabled: true, embeddingEnabled: true, rerankerEntries: 0);
        var downloader = BuildDownloader(handler, settings);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => downloader.EnsureActiveModelsAsync(TestContext.Current.CancellationToken)
        );

        string modelPath = Path.Combine(mTempRoot, "nomic", "model.onnx");
        Assert.False(File.Exists(modelPath));
    }

    [Fact]
    public async Task DownloadFailureCleansUpOrphanTempFile()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var settings = BuildSettings(enabled: true, embeddingEnabled: true, rerankerEntries: 0);
        var downloader = BuildDownloader(handler, settings);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => downloader.EnsureActiveModelsAsync(TestContext.Current.CancellationToken)
        );

        // No .tmp left behind in the model directory — try/finally guarantees cleanup.
        string nomicDir = Path.Combine(mTempRoot, "nomic");
        if (Directory.Exists(nomicDir))
            Assert.Empty(Directory.GetFiles(nomicDir, "*.tmp"));
    }

    [Fact]
    public async Task DownloadZeroByteResponseThrowsAndLeavesNothingBehind()
    {
        var handler = new RecordingHandler(_ => Make200(string.Empty));
        var settings = BuildSettings(enabled: true, embeddingEnabled: true, rerankerEntries: 0);
        var downloader = BuildDownloader(handler, settings);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => downloader.EnsureActiveModelsAsync(TestContext.Current.CancellationToken)
        );
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);

        string nomicDir = Path.Combine(mTempRoot, "nomic");
        string modelPath = Path.Combine(nomicDir, "model.onnx");
        Assert.False(File.Exists(modelPath));
        if (Directory.Exists(nomicDir))
            Assert.Empty(Directory.GetFiles(nomicDir, "*.tmp"));
    }

    private OnnxSettings BuildSettings(bool enabled = true,
                                       bool embeddingEnabled = true,
                                       int rerankerEntries = 1)
    {
        var settings = new OnnxSettings
                           {
                               Enabled = enabled,
                               EmbeddingEnabled = embeddingEnabled,
                               ActiveEmbeddingModel = "nomic",
                               ActiveRerankerModel = rerankerEntries > 0 ? "mxbai" : OnnxSettings.RerankerNoneSentinel,
                               ModelsDir = mTempRoot
                           };
        settings.EmbeddingModels.Add(new EmbeddingModelEntry
                                         {
                                             Name = "nomic",
                                             RepoId = "test/nomic",
                                             ModelFile = "onnx/model_fp16.onnx",
                                             TokenizerFamily = TokenizerFamily.Bert,
                                             VocabFile = "vocab.txt",
                                             Dimensions = 768
                                         });
        if (rerankerEntries > 0)
            settings.RerankerModels.Add(new RerankerModelEntry
                                            {
                                                Name = "mxbai",
                                                RepoId = "test/mxbai",
                                                ModelFile = "onnx/model_quantized.onnx",
                                                TokenizerFamily = TokenizerFamily.SentencePiece,
                                                SpmFile = "spm.model"
                                            });
        return settings;
    }

    private static OnnxModelDownloader BuildDownloader(HttpMessageHandler handler, OnnxSettings settings)
    {
        var factory = new SingleClientFactory(handler);
        return new OnnxModelDownloader(factory, Options.Create(settings),
                                       NullLogger<OnnxModelDownloader>.Instance
                                      );
    }

    private static HttpResponseMessage Make200(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
                   {
                       Content = new StringContent(body)
                   };
    }

    private const string OnnxFileExtension = ".onnx";
    private const string EmptyUrl = "https://example.invalid/";

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            mResponder = responder;
        }

        private readonly Func<HttpRequestMessage, HttpResponseMessage> mResponder;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var response = mResponder(request);
            return Task.FromResult(response);
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public SingleClientFactory(HttpMessageHandler handler)
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
