// OllamaBootstrapperIsReachableTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

/// <summary>
///     Covers the per-call-timeout <c>IsReachableAsync</c> overload that
///     the post-launch poll uses. The behaviour the contract guarantees:
///     true on HTTP success, false on timeout, false on transport error,
///     external cancellation propagates as <see cref="OperationCanceledException" />
///     rather than being swallowed as "not reachable".
/// </summary>
public sealed class OllamaBootstrapperIsReachableTests
{
    [Fact]
    public async Task ReturnsTrueOnHttpSuccess()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var reachable = await OllamaBootstrapper.IsReachableAsync(client,
                                                                  new Uri("http://stub.local"),
                                                                  timeoutSeconds: 5,
                                                                  TestContext.Current.CancellationToken
                                                                 );

        Assert.True(reachable);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ReturnsFalseOnNonSuccessHttpStatus(HttpStatusCode status)
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(status)));

        var reachable = await OllamaBootstrapper.IsReachableAsync(client,
                                                                  new Uri("http://stub.local"),
                                                                  timeoutSeconds: 5,
                                                                  TestContext.Current.CancellationToken
                                                                 );

        Assert.False(reachable);
    }

    [Fact]
    public async Task ReturnsFalseOnTransportError()
    {
        using var client = new HttpClient(new StubHandler((Func<HttpRequestMessage, HttpResponseMessage>) (_ =>
            throw new HttpRequestException("Simulated socket failure")
        )));

        var reachable = await OllamaBootstrapper.IsReachableAsync(client,
                                                                  new Uri("http://stub.local"),
                                                                  timeoutSeconds: 5,
                                                                  TestContext.Current.CancellationToken
                                                                 );

        Assert.False(reachable);
    }

    [Fact]
    public async Task PerCallTimeoutFiresBeforeRequestCompletes()
    {
        // Stub takes 10s to respond. Per-call timeout is 1s.
        // The probe must give up and return false in well under 10s.
        using var client = new HttpClient(new StubHandler(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds: 10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var sw = Stopwatch.StartNew();
        var reachable = await OllamaBootstrapper.IsReachableAsync(client,
                                                                  new Uri("http://stub.local"),
                                                                  timeoutSeconds: 1,
                                                                  TestContext.Current.CancellationToken
                                                                 );
        sw.Stop();

        Assert.False(reachable);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(seconds: 5),
                    $"Per-call timeout took {sw.Elapsed.TotalSeconds:F1}s, expected < 5s");
    }

    [Fact]
    public async Task ExternalCancellationPropagatesAsOperationCanceled()
    {
        using var client = new HttpClient(new StubHandler(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds: 30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // External cancellation should bubble out rather than being
        // collapsed to "not reachable" -- the caller asked us to stop,
        // not just to give a quick answer about reachability.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OllamaBootstrapper.IsReachableAsync(client,
                                                new Uri("http://stub.local"),
                                                timeoutSeconds: 30,
                                                cts.Token
                                               )
        );
    }

    [Fact]
    public async Task NullClientThrows()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            OllamaBootstrapper.IsReachableAsync(NullRef<HttpClient>(),
                                                new Uri("http://stub.local"),
                                                timeoutSeconds: 1,
                                                CancellationToken.None
                                               )
        );
    }

    [Fact]
    public async Task NullEndpointThrows()
    {
        using var client = new HttpClient();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            OllamaBootstrapper.IsReachableAsync(client, NullRef<Uri>(), timeoutSeconds: 1, CancellationToken.None)
        );
    }

    private static T NullRef<T>() where T : class
    {
        T? nullable = null;
        return Unsafe.As<T?, T>(ref nullable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveTimeoutThrows(int timeoutSeconds)
    {
        using var client = new HttpClient();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            OllamaBootstrapper.IsReachableAsync(client,
                                                new Uri("http://stub.local"),
                                                timeoutSeconds,
                                                CancellationToken.None
                                               )
        );
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> mResponder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> sync)
        {
            ArgumentNullException.ThrowIfNull(sync);
            mResponder = _ => Task.FromResult(sync(new HttpRequestMessage()));
        }

        public StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> async)
        {
            ArgumentNullException.ThrowIfNull(async);
            mResponder = async;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                CancellationToken cancellationToken) =>
            mResponder(cancellationToken);
    }
}
