// McpWarmupServicePlaywrightRuntimeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License in the repo root.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Ingestion.Crawling;
using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class McpWarmupServicePlaywrightRuntimeTests
{
    [Fact]
    public async Task VerifyPlaywrightRuntimeAsyncMarksBrowserReady()
    {
        var probe = Substitute.For<IPlaywrightRuntimeProbe>();
        probe.VerifyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var state = new McpWarmupState();
        state.MarkStarted("Starting");

        await McpWarmupService.VerifyPlaywrightRuntimeAsync(probe,
                                                            state,
                                                            NullLogger<McpWarmupService>.Instance,
                                                            TestContext.Current.CancellationToken
                                                           );

        Assert.Equal(McpWarmupService.PhasePlaywrightBrowserReady, state.CurrentPhase);
        Assert.Null(state.LastError);
    }

    [Fact]
    public async Task VerifyPlaywrightRuntimeAsyncMarksBrowserUnavailableAndRethrows()
    {
        var probe = Substitute.For<IPlaywrightRuntimeProbe>();
        probe.VerifyAsync(Arg.Any<CancellationToken>())
             .Returns(Task.FromException(new InvalidOperationException("Chromium is missing.")));
        var state = new McpWarmupState();
        state.MarkStarted("Starting");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                  McpWarmupService.VerifyPlaywrightRuntimeAsync(probe,
                                                                                                                state,
                                                                                                                NullLogger<McpWarmupService>.Instance,
                                                                                                                TestContext.Current.CancellationToken
                                                                                                               )
                                                             );

        Assert.Equal(McpWarmupService.PhasePlaywrightBrowserUnavailable, state.CurrentPhase);
        Assert.Equal("Chromium is missing.", state.LastError);
    }
}