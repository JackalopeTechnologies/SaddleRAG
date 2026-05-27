// MonitorWriteServiceTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Net;
using System.Text;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class MonitorWriteServiceTests
{
    [Fact]
    public async Task CancelJobAsyncPostsToCancelEndpointAndReturnsTrueOn2xx()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var sut = MakeService(handler);

        var result = await sut.CancelJobAsync("job-42", TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/api/monitor/jobs/job-42/cancel", handler.LastUri?.PathAndQuery);
    }

    [Fact]
    public async Task CancelJobAsyncReturnsFalseOnNon2xx()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound);
        var sut = MakeService(handler);

        var result = await sut.CancelJobAsync("missing-job", TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task GetJobSnapshotAsyncTargetsSnapshotEndpointWithGet()
    {
        // 404 returns null without deserializing — exercises the URL +
        // verb contract without needing to satisfy JobTickSnapshot's
        // required-fields set, which would couple this test to that
        // model's evolution.
        var handler = new StubHandler(HttpStatusCode.NotFound);
        var sut = MakeService(handler);

        var snapshot = await sut.GetJobSnapshotAsync("job-1", TestContext.Current.CancellationToken);

        Assert.Null(snapshot);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("/api/monitor/jobs/job-1/snapshot", handler.LastUri?.PathAndQuery);
    }

    [Fact]
    public async Task GetJobSnapshotAsyncReturnsNullOnNotFound()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound);
        var sut = MakeService(handler);

        var snapshot = await sut.GetJobSnapshotAsync("missing-job", TestContext.Current.CancellationToken);

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task RescrapeAsyncPostsVersionInBodyAndReturnsJobIdFromResponse()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
                                      new StringContent("{\"JobId\":\"new-job\"}", Encoding.UTF8, "application/json")
                                     );
        var sut = MakeService(handler);

        var jobId = await sut.RescrapeAsync("aerotech-aeroscript", "current", TestContext.Current.CancellationToken);

        Assert.Equal("new-job", jobId);
        Assert.Equal("/api/monitor/libraries/aerotech-aeroscript/rescrape", handler.LastUri?.PathAndQuery);
        // HttpClient's default JsonSerializerOptions camelCases property
        // names — anonymous { Version = ... } serializes as "version".
        Assert.Contains("\"version\":\"current\"", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RescrubAsyncTargetsRescrubEndpoint()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
                                      new StringContent("{\"JobId\":\"rs-1\"}", Encoding.UTF8, "application/json")
                                     );
        var sut = MakeService(handler);

        var jobId = await sut.RescrubAsync("foo", "1.0", TestContext.Current.CancellationToken);

        Assert.Equal("rs-1", jobId);
        Assert.Equal("/api/monitor/libraries/foo/rescrub", handler.LastUri?.PathAndQuery);
    }

    [Fact]
    public async Task RescrapeAsyncReturnsNullWhenServerDeclines()
    {
        var handler = new StubHandler(HttpStatusCode.Conflict);
        var sut = MakeService(handler);

        var jobId = await sut.RescrapeAsync("foo", "1.0", TestContext.Current.CancellationToken);

        Assert.Null(jobId);
    }

    [Fact]
    public async Task DeleteVersionAsyncSendsDeleteToVersionedEndpoint()
    {
        var handler = new StubHandler(HttpStatusCode.NoContent);
        var sut = MakeService(handler);

        var result = await sut.DeleteVersionAsync("foo", "2.0", TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(HttpMethod.Delete, handler.LastMethod);
        Assert.Equal("/api/monitor/libraries/foo/versions/2.0", handler.LastUri?.PathAndQuery);
    }

    [Fact]
    public async Task DeleteVersionAsyncReturnsFalseOnNon2xx()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound);
        var sut = MakeService(handler);

        var result = await sut.DeleteVersionAsync("missing", "9.9", TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    private static MonitorWriteService MakeService(StubHandler handler) =>
        new MonitorWriteService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });

    /// <summary>
    ///     Captures the outbound request for assertions and returns a
    ///     scripted response. NSubstitute can't mock HttpMessageHandler
    ///     directly because SendAsync is protected; this hand-rolled stub
    ///     is the standard pattern for HttpClient unit tests.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public StubHandler(HttpStatusCode status, HttpContent? content = null)
        {
            mStatus = status;
            mContent = content;
        }

        private readonly HttpStatusCode mStatus;
        private readonly HttpContent? mContent;

        public HttpMethod? LastMethod { get; private set; }
        public Uri? LastUri { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                      CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastUri = request.RequestUri;
            if (request.Content != null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            var response = new HttpResponseMessage(mStatus);
            if (mContent != null)
                response.Content = mContent;
            return response;
        }
    }
}
