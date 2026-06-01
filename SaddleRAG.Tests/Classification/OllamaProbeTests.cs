// OllamaProbeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using System.Text;
using System.Text.Json;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Classification;

/// <summary>
///     Covers <see cref="OllamaProbe" /> against a stubbed
///     <see cref="HttpMessageHandler" />: reachability detection, and the
///     idempotent ensure-model contract (tags-before-pull; no re-pull when
///     model is already present).
/// </summary>
public sealed class OllamaProbeTests
{
    [Fact]
    public async Task IsReachableReturnsTrueWhenOllamaResponds()
    {
        var handler = new PathDispatchHandler();
        handler.Register(TagsPath, _ => TagsOkResponse([]));
        using var client = new HttpClient(handler);
        var probe = MakeProbe(client);

        var result = await probe.IsReachableAsync(TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task IsReachableReturnsFalseWhenConnectionFails()
    {
        var handler = new PathDispatchHandler();
        handler.Register(TagsPath, _ => throw new HttpRequestException("Connection refused"));
        using var client = new HttpClient(handler);
        var probe = MakeProbe(client);

        var result = await probe.IsReachableAsync(TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task EnsureModelDoesNotPullWhenAlreadyPresent()
    {
        var handler = new PathDispatchHandler();
        handler.Register(TagsPath, _ => TagsOkResponse(["phi4-mini:3.8b"]));
        using var client = new HttpClient(handler);
        var probe = MakeProbe(client);

        await probe.EnsureModelAsync("phi4-mini:3.8b", TestContext.Current.CancellationToken);

        Assert.Equal(expected: 0, handler.CallCount(PullPath));
    }

    [Fact]
    public async Task EnsureModelPullsWhenAbsent()
    {
        var handler = new PathDispatchHandler();
        handler.Register(TagsPath, _ => TagsOkResponse([]));
        handler.Register(PullPath, _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var probe = MakeProbe(client);

        await probe.EnsureModelAsync("phi4-mini:3.8b", TestContext.Current.CancellationToken);

        Assert.Equal(expected: 1, handler.CallCount(PullPath));
        Assert.Contains("phi4-mini", handler.LastBody(PullPath));
    }

    [Fact]
    public async Task EnsureModelOnUnreachableOllamaThrowsClearError()
    {
        var handler = new PathDispatchHandler();
        handler.Register(TagsPath, _ => throw new HttpRequestException("Connection refused"));
        using var client = new HttpClient(handler);
        var probe = MakeProbe(client);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            probe.EnsureModelAsync("phi4-mini:3.8b", TestContext.Current.CancellationToken)
        );

        Assert.Contains("not reachable", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ollama.com", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureModelDoesNotPullWhenPresentWithLatestTag()
    {
        var handler = new PathDispatchHandler();
        handler.Register(TagsPath, _ => TagsOkResponse(["phi4-mini:latest"]));
        using var client = new HttpClient(handler);
        var probe = MakeProbe(client);

        await probe.EnsureModelAsync("phi4-mini", TestContext.Current.CancellationToken);

        Assert.Equal(expected: 0, handler.CallCount(PullPath));
    }

    [Fact]
    public async Task EnsureModelDoesNotPullWhenPresentWithTagVariant()
    {
        var handler = new PathDispatchHandler();
        handler.Register(TagsPath, _ => TagsOkResponse(["phi4:14b"]));
        using var client = new HttpClient(handler);
        var probe = MakeProbe(client);

        await probe.EnsureModelAsync("phi4", TestContext.Current.CancellationToken);

        Assert.Equal(expected: 0, handler.CallCount(PullPath));
    }

    [Fact]
    public async Task EnsureModelPullBodyContainsModelName()
    {
        var handler = new PathDispatchHandler();
        handler.Register(TagsPath, _ => TagsOkResponse([]));
        handler.Register(PullPath, _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var probe = MakeProbe(client);

        await probe.EnsureModelAsync("nomic-embed-text", TestContext.Current.CancellationToken);

        var body = handler.LastBody(PullPath);
        Assert.Contains("nomic-embed-text", body);
    }

    private static OllamaProbe MakeProbe(HttpClient client) =>
        new OllamaProbe(client, new OllamaSettings { Endpoint = "http://stub.local" });

    private static HttpResponseMessage TagsOkResponse(IReadOnlyList<string> modelNames)
    {
        var models = modelNames.Select(n => new { name = n }).ToArray();
        var body = JsonSerializer.Serialize(new { models });
        return new HttpResponseMessage(HttpStatusCode.OK)
                   {
                       Content = new StringContent(body, Encoding.UTF8, "application/json")
                   };
    }

    private const string TagsPath = "/api/tags";
    private const string PullPath = "/api/pull";

    /// <summary>
    ///     Dispatches requests by path, recording call counts and last body
    ///     per path for assertion.
    /// </summary>
    private sealed class PathDispatchHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> mHandlers = new();
        private readonly Dictionary<string, int> mCallCounts = new();
        private readonly Dictionary<string, string> mLastBodies = new();

        public void Register(string path, Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            ArgumentNullException.ThrowIfNull(handler);
            mHandlers[path] = handler;
        }

        public int CallCount(string path) =>
            mCallCounts.TryGetValue(path, out var count) ? count : 0;

        public string LastBody(string path) =>
            mLastBodies.TryGetValue(path, out var body) ? body : string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                      CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request.RequestUri);

            var path = request.RequestUri.AbsolutePath;
            mCallCounts[path] = CallCount(path) + 1;

            if (request.Content != null)
                mLastBodies[path] = await request.Content.ReadAsStringAsync(cancellationToken);

            if (!mHandlers.TryGetValue(path, out var handler))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            return handler(request);
        }
    }
}
