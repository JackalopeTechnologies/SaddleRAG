// McpToolExceptionFilterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
                                                                          () => ValueTask.FromResult(expected),
                                                                          TestContext.Current.CancellationToken
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
                                                                          Throw,
                                                                          TestContext.Current.CancellationToken
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
                                                                          Throw,
                                                                          TestContext.Current.CancellationToken
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
                                                                                             Throw,
                                                                                             TestContext.Current.CancellationToken
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
                                                                                                           Throw,
                                                                                                           TestContext.Current.CancellationToken
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
                                                                                                          Throw,
                                                                                                          TestContext.Current.CancellationToken
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

        await McpToolExceptionFilter.ExecuteAsync("list_libraries", services: null, Run, TestContext.Current.CancellationToken);

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

        CallToolResult result = await McpToolExceptionFilter.ExecuteAsync("list_libraries", services: null, Throw, TestContext.Current.CancellationToken);

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

        CallToolResult result = await McpToolExceptionFilter.ExecuteAsync("search_docs", services: null, Throw, TestContext.Current.CancellationToken);

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

        CallToolResult result = await McpToolExceptionFilter.ExecuteAsync("list_libraries", services, Throw, TestContext.Current.CancellationToken);

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
                                                                                                 Throw,
                                                                                                 TestContext.Current.CancellationToken
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

        CallToolResult result = await McpToolExceptionFilter.ExecuteAsync("scrape_docs", services, Throw, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsyncReturnsTheRestartReportAndSkipsTheToolWhenRecoveryFails()
    {
        var recovery = CreateRecovery(new FakeDatabase(), new FakeServiceControl(MongoServiceState.NotInstalled));
        var services = new ServiceCollection().AddLogging().AddSingleton(recovery).BuildServiceProvider();
        int calls = 0;

        ValueTask<CallToolResult> Next()
        {
            calls++;
            return ValueTask.FromResult(new CallToolResult());
        }

        CallToolResult result = await McpToolExceptionFilter.ExecuteAsync("list_libraries",
                                                                          services,
                                                                          Next,
                                                                          TestContext.Current.CancellationToken
                                                                         );

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("is not installed", text.Text, StringComparison.Ordinal);
        Assert.Equal(expected: 0, calls);
    }

    [Fact]
    public async Task ExecuteAsyncRunsTheToolOnceWhenRecoveryRestartsTheService()
    {
        var database = new FakeDatabase();
        var service = new FakeServiceControl(MongoServiceState.Stopped) { OnStart = database.BeginAnswering };
        var recovery = CreateRecovery(database, service);
        var services = new ServiceCollection().AddLogging().AddSingleton(recovery).BuildServiceProvider();
        var expected = new CallToolResult();
        int calls = 0;

        ValueTask<CallToolResult> Next()
        {
            calls++;
            return ValueTask.FromResult(expected);
        }

        CallToolResult result = await McpToolExceptionFilter.ExecuteAsync("list_libraries",
                                                                          services,
                                                                          Next,
                                                                          TestContext.Current.CancellationToken
                                                                         );

        Assert.Same(expected, result);
        Assert.Equal(expected: 1, calls);
        Assert.Equal(expected: 1, service.StartCount);
    }

    private static DatabaseRecovery CreateRecovery(FakeDatabase database, FakeServiceControl service)
    {
        IReadOnlyList<IRecoverableDatabase> databases = [database];
        var timings = new DatabaseRecoveryTimings(RestartWait: TimeSpan.FromMilliseconds(milliseconds: 300),
                                                  FailurePause: TimeSpan.FromMilliseconds(milliseconds: 500),
                                                  PingTimeout: TimeSpan.FromMilliseconds(milliseconds: 50),
                                                  PingInterval: TimeSpan.FromMilliseconds(milliseconds: 20)
                                                 );
        return new DatabaseRecovery(() => databases,
                                    service,
                                    Options.Create(new SaddleRagDbSettings()),
                                    TimeProvider.System,
                                    NullLogger<DatabaseRecovery>.Instance,
                                    timings
                                   );
    }

    /// <summary>A local database that is known down until <see cref="BeginAnswering" />.</summary>
    private sealed class FakeDatabase : IRecoverableDatabase
    {
        private volatile bool mAnswering;
        private volatile bool mIsKnownDown = true;

        public string Endpoint => "localhost:27017";

        public bool IsLocal => true;

        public bool IsKnownDown => mIsKnownDown;

        public Task CheckHealthAsync(CancellationToken ct)
        {
            Task result;
            if (mAnswering)
            {
                mIsKnownDown = false;
                result = Task.CompletedTask;
            }
            else
                result = Task.FromException(new TimeoutException("No answer."));

            return result;
        }

        public void BeginAnswering() => mAnswering = true;
    }

    /// <summary>A service that reports a fixed state and counts start requests.</summary>
    private sealed class FakeServiceControl(MongoServiceState state) : IMongoServiceControl
    {
        private int mStartCount;
        private MongoServiceState mState = state;

        public Action? OnStart { get; init; }

        public int StartCount => Volatile.Read(ref mStartCount);

        public MongoServiceState GetState(string serviceName) => mState;

        public void Start(string serviceName)
        {
            Interlocked.Increment(ref mStartCount);
            mState = MongoServiceState.Running;
            OnStart?.Invoke();
        }
    }
}
