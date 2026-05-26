// OllamaBootstrapperWarmTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

/// <summary>
///     Locks in <see cref="OllamaBootstrapper.WarmSingleModelAsync" />'s
///     contract: the warm call must verify Ollama actually returned a
///     READY token, must throw on missing/wrong-body/timeout/HTTP-error
///     so the warmup host can mark warmup Failed, and must round-trip
///     the configured classifier-priming prompt into the outgoing JSON
///     body (so future drift on either side is caught by tests instead
///     of silent "warm" passes that leave the model cold).
/// </summary>
public sealed class OllamaBootstrapperWarmTests
{
    [Fact]
    public async Task SuccessfulWarmWithReadyResponseReturnsCleanly()
    {
        var handler = new StubHttpMessageHandler(_ => OllamaResponse(readyToken: "READY"));
        using var client = new HttpClient(handler);

        var bootstrapper = MakeBootstrapper();
        var endpoint = new Uri("http://stub.local/api/generate");

        await bootstrapper.WarmSingleModelAsync(client,
                                                endpoint,
                                                "phi4-mini:3.8b",
                                                timeoutSeconds: 30,
                                                TestContext.Current.CancellationToken
                                               );

        Assert.Equal(expected: 1, handler.RequestCount);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Contains("phi4-mini", handler.LastBody);
    }

    [Fact]
    public async Task CaseInsensitiveReadyTokenStillAccepted()
    {
        var handler = new StubHttpMessageHandler(_ => OllamaResponse(readyToken: "ready"));
        using var client = new HttpClient(handler);

        await MakeBootstrapper().WarmSingleModelAsync(client,
                                                      MakeEndpoint(),
                                                      "phi4-mini:3.8b",
                                                      timeoutSeconds: 30,
                                                      TestContext.Current.CancellationToken
                                                     );
    }

    [Fact]
    public async Task EmptyResponseBodyThrowsInvalidOperation()
    {
        var handler = new StubHttpMessageHandler(_ => OllamaResponse(readyToken: string.Empty));
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MakeBootstrapper().WarmSingleModelAsync(client,
                                                    MakeEndpoint(),
                                                    "phi4-mini:3.8b",
                                                    timeoutSeconds: 30,
                                                    TestContext.Current.CancellationToken
                                                   )
        );

        Assert.Contains("phi4-mini", ex.Message);
        Assert.Contains("READY", ex.Message);
    }

    [Fact]
    public async Task WrongResponseTokenThrowsInvalidOperation()
    {
        var handler = new StubHttpMessageHandler(_ =>
            OllamaResponse(readyToken: "I do not understand")
        );
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MakeBootstrapper().WarmSingleModelAsync(client,
                                                    MakeEndpoint(),
                                                    "phi4-mini:3.8b",
                                                    timeoutSeconds: 30,
                                                    TestContext.Current.CancellationToken
                                                   )
        );
    }

    [Fact]
    public async Task HttpErrorPropagates()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("ollama error")
            }
        );
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            MakeBootstrapper().WarmSingleModelAsync(client,
                                                    MakeEndpoint(),
                                                    "phi4-mini:3.8b",
                                                    timeoutSeconds: 30,
                                                    TestContext.Current.CancellationToken
                                                   )
        );
    }

    [Fact]
    public async Task TaskCanceledFromHttpTimeoutTranslatesToTimeoutException()
    {
        // Simulate the HttpClient.Timeout-fired path: the handler throws
        // TaskCanceledException without the caller's CT being cancelled,
        // which is exactly the shape `HttpClient` produces when its own
        // timeout fires. WarmSingleModelAsync must translate that into a
        // TimeoutException with an actionable message rather than letting
        // OperationCanceledException leak out as a generic cancel.
        var handler = new StubHttpMessageHandler((Func<HttpRequestMessage, HttpResponseMessage>) (_ =>
            throw new TaskCanceledException("Simulated HttpClient.Timeout")
        ));
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            MakeBootstrapper().WarmSingleModelAsync(client,
                                                    MakeEndpoint(),
                                                    "phi4-mini:3.8b",
                                                    timeoutSeconds: 1,
                                                    TestContext.Current.CancellationToken
                                                   )
        );

        Assert.Contains("phi4-mini", ex.Message);
        Assert.Contains("did not complete", ex.Message);
    }

    [Fact]
    public async Task ExternalCancellationDoesNotMaskAsTimeout()
    {
        var handler = new StubHttpMessageHandler(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds: 30), ct);
            return OllamaResponse(readyToken: "READY");
        });
        using var client = new HttpClient(handler);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MakeBootstrapper().WarmSingleModelAsync(client,
                                                    MakeEndpoint(),
                                                    "phi4-mini:3.8b",
                                                    timeoutSeconds: 30,
                                                    cts.Token
                                                   )
        );
    }

    [Fact]
    public async Task PayloadIncludesKeepAliveMinusOne()
    {
        var handler = new StubHttpMessageHandler(_ => OllamaResponse(readyToken: "READY"));
        using var client = new HttpClient(handler);

        await MakeBootstrapper().WarmSingleModelAsync(client,
                                                      MakeEndpoint(),
                                                      "phi4-mini:3.8b",
                                                      timeoutSeconds: 30,
                                                      TestContext.Current.CancellationToken
                                                     );

        // Ollama's keep_alive contract: -1 means "stay resident forever".
        // Anything else and the model would drop out of VRAM during idle
        // gaps and the first real classify call would re-pay cold load.
        Assert.Contains("\"keep_alive\":-1", handler.LastBody);
    }

    [Fact]
    public async Task PayloadIncludesClassifierPrimingContent()
    {
        var handler = new StubHttpMessageHandler(_ => OllamaResponse(readyToken: "READY"));
        using var client = new HttpClient(handler);

        await MakeBootstrapper().WarmSingleModelAsync(client,
                                                      MakeEndpoint(),
                                                      "phi4-mini:3.8b",
                                                      timeoutSeconds: 30,
                                                      TestContext.Current.CancellationToken
                                                     );

        // If the warm prompt drifts to a no-op (like the original "."
        // prompt that left the model loaded but unverified), the warm
        // contract is broken. Lock in the classifier-priming shape.
        Assert.Contains("classifier", handler.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("READY", handler.LastBody);
    }

    private static OllamaBootstrapper MakeBootstrapper()
    {
        var settings = Options.Create(new OllamaSettings
                                          {
                                              Endpoint = "http://stub.local",
                                              ClassificationModels =
                                              [
                                                  new OllamaModelEntry
                                                  {
                                                      Name = "phi4-mini:3.8b",
                                                      Description = "stub classifier"
                                                  }
                                              ],
                                              ReconModels =
                                              [
                                                  new OllamaModelEntry
                                                  {
                                                      Name = "phi4-mini:3.8b",
                                                      Description = "stub recon"
                                                  }
                                              ]
                                          }
                                     );
        return new OllamaBootstrapper(settings, NullLogger<OllamaBootstrapper>.Instance);
    }

    private static Uri MakeEndpoint() => new Uri("http://stub.local/api/generate");

    private static HttpResponseMessage OllamaResponse(string readyToken)
    {
        var body = JsonSerializer.Serialize(new
                                                {
                                                    model = "phi4-mini:3.8b",
                                                    response = readyToken,
                                                    done = true
                                                }
                                           );
        return new HttpResponseMessage(HttpStatusCode.OK)
                   {
                       Content = new StringContent(body, Encoding.UTF8, "application/json")
                   };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> mResponder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> sync)
        {
            ArgumentNullException.ThrowIfNull(sync);
            mResponder = _ => Task.FromResult(sync(LastRequest ?? new HttpRequestMessage()));
        }

        public StubHttpMessageHandler(Func<CancellationToken, Task<HttpResponseMessage>> async)
        {
            ArgumentNullException.ThrowIfNull(async);
            mResponder = async;
        }

        public int RequestCount { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string LastBody { get; private set; } = string.Empty;
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                     CancellationToken cancellationToken)
        {
            RequestCount++;
            LastMethod = request.Method;
            LastRequest = request;
            if (request.Content != null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return await mResponder(cancellationToken);
        }
    }
}
