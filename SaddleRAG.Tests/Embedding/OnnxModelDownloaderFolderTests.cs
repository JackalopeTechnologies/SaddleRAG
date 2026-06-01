// OnnxModelDownloaderFolderTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

/// <summary>
///     Unit tests for <see cref="OnnxModelDownloader.DownloadModelFolderAsync" />.
///     All HTTP is mocked — no real network calls are made. Each test creates an
///     isolated temp directory and cleans up on dispose.
/// </summary>
public sealed class OnnxModelDownloaderFolderTests : IDisposable
{
    public OnnxModelDownloaderFolderTests()
    {
        mTempRoot = Path.Combine(Path.GetTempPath(), $"onnx-folder-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mTempRoot);
    }

    private readonly string mTempRoot;

    public void Dispose()
    {
        if (Directory.Exists(mTempRoot))
            Directory.Delete(mTempRoot, recursive: true);
    }

    [Fact]
    public async Task DownloadModelFolderAsyncWritesAllListedFilesToTargetDir()
    {
        string treeJson = BuildTreeJson(
            ("file", "gpu/gpu-int4-rtn-block-32/genai_config.json", 512),
            ("file", "gpu/gpu-int4-rtn-block-32/model.onnx", 1024),
            ("file", "gpu/gpu-int4-rtn-block-32/tokenizer.json", 256)
        );

        var handler = new FolderDownloadHandler(treeJson, fileByte: 0xAB);
        var downloader = BuildDownloader(handler);
        string targetDir = Path.Combine(mTempRoot, "phi4-cuda");

        await downloader.DownloadModelFolderAsync(
            repoId: "microsoft/Phi-4-mini-instruct-onnx",
            modelFolder: "gpu/gpu-int4-rtn-block-32",
            targetDir: targetDir,
            ct: TestContext.Current.CancellationToken
        );

        Assert.True(File.Exists(Path.Combine(targetDir, "genai_config.json")));
        Assert.True(File.Exists(Path.Combine(targetDir, "model.onnx")));
        Assert.True(File.Exists(Path.Combine(targetDir, "tokenizer.json")));
    }

    [Fact]
    public async Task DownloadModelFolderAsyncWritesCorrectContentForEachFile()
    {
        string treeJson = BuildTreeJson(
            ("file", "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/config.json", 128),
            ("file", "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/vocab.json", 64)
        );

        const string fileContent = "FAKE_MODEL_BYTES";
        var handler = new FolderDownloadHandler(treeJson, fileContent: fileContent);
        var downloader = BuildDownloader(handler);
        string targetDir = Path.Combine(mTempRoot, "phi4-cpu");

        await downloader.DownloadModelFolderAsync(
            repoId: "microsoft/Phi-4-mini-instruct-onnx",
            modelFolder: "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4",
            targetDir: targetDir,
            ct: TestContext.Current.CancellationToken
        );

        string configContent = await File.ReadAllTextAsync(
            Path.Combine(targetDir, "config.json"),
            TestContext.Current.CancellationToken
        );
        string vocabContent = await File.ReadAllTextAsync(
            Path.Combine(targetDir, "vocab.json"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(fileContent, configContent);
        Assert.Equal(fileContent, vocabContent);
    }

    [Fact]
    public async Task DownloadModelFolderAsyncSkipsDirectoryEntries()
    {
        string treeJson = BuildTreeJson(
            ("directory", "gpu/gpu-int4-rtn-block-32/subdir", 0),
            ("file", "gpu/gpu-int4-rtn-block-32/genai_config.json", 128)
        );

        var handler = new FolderDownloadHandler(treeJson, fileByte: 0x01);
        var downloader = BuildDownloader(handler);
        string targetDir = Path.Combine(mTempRoot, "phi4-dir-test");

        await downloader.DownloadModelFolderAsync(
            repoId: "microsoft/Phi-4-mini-instruct-onnx",
            modelFolder: "gpu/gpu-int4-rtn-block-32",
            targetDir: targetDir,
            ct: TestContext.Current.CancellationToken
        );

        Assert.Single(handler.FileDownloadRequests);
        Assert.True(File.Exists(Path.Combine(targetDir, "genai_config.json")));
        Assert.False(Directory.Exists(Path.Combine(targetDir, "subdir")));
    }

    [Fact]
    public async Task DownloadModelFolderAsyncSkipsFilesAlreadyPresent()
    {
        string treeJson = BuildTreeJson(
            ("file", "gpu/gpu-int4-rtn-block-32/tokenizer.json", 64),
            ("file", "gpu/gpu-int4-rtn-block-32/model.onnx", 1024)
        );

        string targetDir = Path.Combine(mTempRoot, "phi4-skip-test");
        Directory.CreateDirectory(targetDir);
        await File.WriteAllTextAsync(
            Path.Combine(targetDir, "tokenizer.json"),
            "ALREADY_HERE",
            TestContext.Current.CancellationToken
        );

        var handler = new FolderDownloadHandler(treeJson, fileContent: "DOWNLOADED");
        var downloader = BuildDownloader(handler);

        await downloader.DownloadModelFolderAsync(
            repoId: "microsoft/Phi-4-mini-instruct-onnx",
            modelFolder: "gpu/gpu-int4-rtn-block-32",
            targetDir: targetDir,
            ct: TestContext.Current.CancellationToken
        );

        Assert.Single(handler.FileDownloadRequests);
        Assert.Equal("ALREADY_HERE",
                     await File.ReadAllTextAsync(Path.Combine(targetDir, "tokenizer.json"),
                                                 TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(targetDir, "model.onnx")));
    }

    [Fact]
    public async Task DownloadModelFolderAsyncUsesCorrectTreeApiUrl()
    {
        const string repoId = "microsoft/Phi-4-mini-instruct-onnx";
        const string modelFolder = "gpu/gpu-int4-rtn-block-32";

        string treeJson = BuildTreeJson(
            ("file", "gpu/gpu-int4-rtn-block-32/genai_config.json", 64)
        );

        var handler = new FolderDownloadHandler(treeJson, fileByte: 0x01);
        var downloader = BuildDownloader(handler);
        string targetDir = Path.Combine(mTempRoot, "url-check");

        await downloader.DownloadModelFolderAsync(
            repoId: repoId,
            modelFolder: modelFolder,
            targetDir: targetDir,
            ct: TestContext.Current.CancellationToken
        );

        string expectedTreeUrl = string.Format(OnnxModelDownloader.HuggingFaceTreeUrlFormat,
                                               repoId, modelFolder);
        Assert.Contains(handler.AllRequests, r => r.RequestUri?.ToString() == expectedTreeUrl);
    }

    [Fact]
    public async Task DownloadModelFolderAsyncUsesResolveUrlForFileDownload()
    {
        const string repoId = "microsoft/Phi-4-mini-instruct-onnx";
        const string modelFolder = "gpu/gpu-int4-rtn-block-32";
        const string filePath = "gpu/gpu-int4-rtn-block-32/genai_config.json";

        string treeJson = BuildTreeJson(("file", filePath, 64));

        var handler = new FolderDownloadHandler(treeJson, fileByte: 0x01);
        var downloader = BuildDownloader(handler);
        string targetDir = Path.Combine(mTempRoot, "resolve-url-check");

        await downloader.DownloadModelFolderAsync(
            repoId: repoId,
            modelFolder: modelFolder,
            targetDir: targetDir,
            ct: TestContext.Current.CancellationToken
        );

        string expectedResolveUrl = $"https://huggingface.co/{repoId}/resolve/main/{filePath}";
        Assert.Contains(handler.AllRequests, r => r.RequestUri?.ToString() == expectedResolveUrl);
    }

    [Fact]
    public async Task DownloadModelFolderAsyncEmptyTreeDownloadsNothing()
    {
        string treeJson = "[]";
        var handler = new FolderDownloadHandler(treeJson, fileByte: 0x01);
        var downloader = BuildDownloader(handler);
        string targetDir = Path.Combine(mTempRoot, "empty-tree");

        await downloader.DownloadModelFolderAsync(
            repoId: "microsoft/Phi-4-mini-instruct-onnx",
            modelFolder: "gpu/gpu-int4-rtn-block-32",
            targetDir: targetDir,
            ct: TestContext.Current.CancellationToken
        );

        Assert.Empty(handler.FileDownloadRequests);
        Assert.True(Directory.Exists(targetDir));
    }

    [Fact]
    public async Task DownloadModelFolderAsyncTreeApiFailurePropagates()
    {
        var handler = new AlwaysFailHandler(HttpStatusCode.NotFound);
        var downloader = BuildDownloader(handler);
        string targetDir = Path.Combine(mTempRoot, "tree-fail");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => downloader.DownloadModelFolderAsync(
                repoId: "microsoft/Phi-4-mini-instruct-onnx",
                modelFolder: "gpu/gpu-int4-rtn-block-32",
                targetDir: targetDir,
                ct: TestContext.Current.CancellationToken
            )
        );
    }

    private OnnxModelDownloader BuildDownloader(HttpMessageHandler handler)
    {
        var settings = new OnnxSettings { ModelsDir = mTempRoot };
        var factory = new SingleClientFactory(handler);
        return new OnnxModelDownloader(factory,
                                       Options.Create(settings),
                                       NullLogger<OnnxModelDownloader>.Instance);
    }

    private static string BuildTreeJson(params (string type, string path, long size)[] entries)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < entries.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            var (type, path, size) = entries[i];
            sb.Append($"{{\"type\":\"{type}\",\"path\":\"{path}\",\"size\":{size}}}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    ///     Handles both the tree API call (returns canned JSON) and
    ///     individual file-resolve calls (returns canned byte content).
    ///     Distinguishes them by the presence of <c>/api/models/</c> in
    ///     the URL — the tree API path — versus the resolve URL pattern.
    /// </summary>
    private sealed class FolderDownloadHandler : HttpMessageHandler
    {
        public FolderDownloadHandler(string treeJson, byte fileByte = 0xFF)
        {
            mTreeJson = treeJson;
            mFileByte = fileByte;
            mFileContent = null;
        }

        public FolderDownloadHandler(string treeJson, string fileContent)
        {
            mTreeJson = treeJson;
            mFileByte = 0;
            mFileContent = fileContent;
        }

        private readonly string mTreeJson;
        private readonly byte mFileByte;
        private readonly string? mFileContent;

        public List<HttpRequestMessage> AllRequests { get; } = [];
        public List<HttpRequestMessage> FileDownloadRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken ct)
        {
            AllRequests.Add(request);
            string url = (request.RequestUri ?? new Uri(FallbackUrl)).ToString();

            HttpResponseMessage response = url.Contains(TreeApiSegment, StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                  {
                      Content = new StringContent(mTreeJson, Encoding.UTF8, ApplicationJson)
                  }
                : BuildFileResponse(request);

            return Task.FromResult(response);
        }

        private HttpResponseMessage BuildFileResponse(HttpRequestMessage request)
        {
            FileDownloadRequests.Add(request);
            HttpContent content = mFileContent != null
                ? new StringContent(mFileContent)
                : new ByteArrayContent([mFileByte]);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private const string TreeApiSegment = "/api/models/";
        private const string ApplicationJson = "application/json";
        private const string FallbackUrl = "https://example.invalid/";
    }

    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        public AlwaysFailHandler(HttpStatusCode statusCode)
        {
            mStatusCode = statusCode;
        }

        private readonly HttpStatusCode mStatusCode;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken ct)
        {
            return Task.FromResult(new HttpResponseMessage(mStatusCode));
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
