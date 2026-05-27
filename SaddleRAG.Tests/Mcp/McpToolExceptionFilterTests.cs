// McpToolExceptionFilterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class McpToolExceptionFilterTests
{
    [Fact]
    public async Task ExecuteAsyncPassesThroughSuccessfulResult()
    {
        var expected = new CallToolResult
                           {
                               Content = [new TextContentBlock { Text = "ok" }],
                               IsError = false
                           };

        CallToolResult result = await McpToolExceptionFilter.ExecuteAsync("scrape_docs",
                                                                          services: null,
                                                                          () => ValueTask.FromResult(expected)
                                                                         );

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task ExecuteAsyncConvertsArgumentExceptionToIsErrorResult()
    {
        ValueTask<CallToolResult> Throw()
        {
            throw new ArgumentException(message: "The value cannot be an empty string.", paramName: "libraryId");
        }

        CallToolResult result = await McpToolExceptionFilter.ExecuteAsync("scrape_docs",
                                                                          services: null,
                                                                          Throw
                                                                         );

        Assert.True(result.IsError);
        var content = Assert.Single(result.Content);
        var text = Assert.IsType<TextContentBlock>(content);
        Assert.Contains("scrape_docs", text.Text);
        Assert.Contains("empty string", text.Text);
    }

    [Fact]
    public async Task ExecuteAsyncConvertsArgumentNullExceptionToIsErrorResult()
    {
        ValueTask<CallToolResult> Throw()
        {
            throw new ArgumentNullException("libraryId");
        }

        CallToolResult result = await McpToolExceptionFilter.ExecuteAsync("scrape_docs",
                                                                          services: null,
                                                                          Throw
                                                                         );

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("scrape_docs", text.Text);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotCatchMcpException()
    {
        ValueTask<CallToolResult> Throw()
        {
            throw new McpException("explicit MCP error");
        }

        await Assert.ThrowsAsync<McpException>(async () =>
                                                   await McpToolExceptionFilter.ExecuteAsync("scrape_docs",
                                                                                             services: null,
                                                                                             Throw
                                                                                            )
                                              );
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotCatchOperationCanceledException()
    {
        ValueTask<CallToolResult> Throw()
        {
            throw new OperationCanceledException();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                                                                 await McpToolExceptionFilter.ExecuteAsync("scrape_docs",
                                                                                                           services: null,
                                                                                                           Throw
                                                                                                          )
                                                            );
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotCatchUnrelatedException()
    {
        ValueTask<CallToolResult> Throw()
        {
            throw new InvalidOperationException("unexpected");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                                                                await McpToolExceptionFilter.ExecuteAsync("scrape_docs",
                                                                                                          services: null,
                                                                                                          Throw
                                                                                                         )
                                                           );
    }

    [Fact]
    public void BuildArgumentErrorResultUsesUnknownPlaceholderWhenToolNameMissing()
    {
        var ex = new ArgumentException("bad");

        CallToolResult result = McpToolExceptionFilter.BuildArgumentErrorResult(toolName: null, ex);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("<unknown>", text.Text);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsyncLogsThroughInjectedLoggerFactory()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();

        ValueTask<CallToolResult> Throw()
        {
            throw new ArgumentException("nope", "libraryId");
        }

        CallToolResult result = await McpToolExceptionFilter.ExecuteAsync("scrape_docs", services, Throw);

        Assert.True(result.IsError);
    }
}
