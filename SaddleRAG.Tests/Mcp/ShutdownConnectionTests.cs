// ShutdownConnectionTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using SaddleRAG.Mcp;

namespace SaddleRAG.Tests.Mcp;

public sealed class ShutdownConnectionTests
{
    [Theory]
    [InlineData("/mcp", true)]
    [InlineData("/_blazor", true)]
    [InlineData("/monitor/hub", true)]
    [InlineData("/health", false)]
    public async Task ShutdownClosesLongLivedConnectionsAndLetsOrdinaryRequestsFinish(string path, bool closes)
    {
        using var stopping = new CancellationTokenSource();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(stopping.Token);
        var requestLifetime = Substitute.For<IHttpRequestLifetimeFeature>();
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Features.Set(requestLifetime);
        var middleware = new ShutdownConnectionMiddleware(_ => stopping.CancelAsync(), lifetime);

        await middleware.InvokeAsync(context);

        requestLifetime.Received(closes ? 1 : 0).Abort();
    }

    [Fact]
    public async Task FinishedRequestsAreNotAbortedByLaterShutdown()
    {
        using var stopping = new CancellationTokenSource();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(stopping.Token);
        var requestLifetime = Substitute.For<IHttpRequestLifetimeFeature>();
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Features.Set(requestLifetime);
        var middleware = new ShutdownConnectionMiddleware(_ => Task.CompletedTask, lifetime);

        await middleware.InvokeAsync(context);
        await stopping.CancelAsync();

        requestLifetime.DidNotReceive().Abort();
    }
}
