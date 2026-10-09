// McpToolExceptionFilterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using SaddleRAG.Database;
using SaddleRAG.Mcp;
using SaddleRAG.Tests.Database;

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
    public async Task ExecuteAsyncRunsTheToolInsideADatabaseFailFastScopeThatEndsWithTheCall()
    {
        bool activeDuringCall = false;

        ValueTask<CallToolResult> Run()
        {
            activeDuringCall = DatabaseFailFastScope.IsActive;
            return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] });
        }

        await McpToolExceptionFilter.ExecuteAsync("list_libraries", services: null, Run);

        Assert.True(activeDuringCall);
        Assert.False(DatabaseFailFastScope.IsActive);
    }

    [Fact]
    public async Task ExecuteAsyncReportsAnUnreachableDatabaseByEndpointInsteadOfTheGenericError()
    {
        var unavailable = new DatabaseUnavailableException("localhost:27017", new IOException("refused"));

        ValueTask<CallToolResult> Throw()
        {
            throw unavailable;
        }

        CallToolResult result = await McpToolExceptionFilter.ExecuteAsync("list_libraries", services: null, Throw);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal(unavailable.Message, text.Text);
    }

    [Fact]
    public async Task ExecuteAsyncReportsAFailedMongoConnectionAsAnUnreachableDatabase()
    {
        var connectionId = new ConnectionId(new ServerId(new ClusterId(value: 1), new DnsEndPoint("localhost", port: 27017)));

        ValueTask<CallToolResult> Throw()
        {
            throw new MongoConnectionException(connectionId, "An exception occurred while opening a connection to the server.");
        }

        CallToolResult result = await McpToolExceptionFilter.ExecuteAsync("search_docs", services: null, Throw);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("localhost:27017", text.Text);
        Assert.Contains("unreachable", text.Text);
    }

    [Fact]
    public async Task ExecuteAsyncReportsTheSelectionTimeoutWhileTheDatabaseIsKnownUnreachable()
    {
        (var factory, string endpoint) =
            await UnreachableDatabase.CreateFactoryWithUnreachableDefaultAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection().AddLogging().AddSingleton(factory).BuildServiceProvider();

        ValueTask<CallToolResult> Throw()
        {
            throw new TimeoutException("A timeout occurred after 30000ms selecting a server.");
        }

        CallToolResult result = await McpToolExceptionFilter.ExecuteAsync("list_libraries", services, Throw);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains(endpoint, text.Text);
    }

    [Fact]
    public async Task ExecuteAsyncLetsATimeoutThroughWhenNoDatabaseIsUnreachable()
    {
        var factory = new SaddleRagDbContextFactory(Options.Create(new SaddleRagDbSettings()));
        var services = new ServiceCollection().AddLogging().AddSingleton(factory).BuildServiceProvider();

        ValueTask<CallToolResult> Throw()
        {
            throw new TimeoutException("unrelated timeout");
        }

        await Assert.ThrowsAsync<TimeoutException>(async () =>
                                                       await McpToolExceptionFilter.ExecuteAsync("list_libraries",
                                                                                                 services,
                                                                                                 Throw
                                                                                                )
                                                  );
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
