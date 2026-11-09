// MonitorDatabaseRecoveryMiddlewareTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaddleRAG.Database;
using SaddleRAG.Mcp;
using SaddleRAG.Tests.Database;

#endregion

namespace SaddleRAG.Tests.Mcp;

/// <summary>
///     Pins what a Monitor page request gets when the database is down: SaddleRAG first
///     tries to restart a stopped local MongoDB service, and if the database still cannot
///     be reached the request gets a short, plain 503 reply instead of an error page.
/// </summary>
public sealed class MonitorDatabaseRecoveryMiddlewareTests
{
    [Fact]
    public async Task ANonMonitorPathNeverTouchesRecovery()
    {
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped);
        var harness = CreateHarness(service);

        var context = await InvokeAsync(harness, McpPath);

        Assert.Equal(expected: 1, harness.NextCalls);
        Assert.Equal(expected: 0, service.GetStateCount);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task AMonitorPathRunsTheRequestOnceWhenTheDatabaseIsFine()
    {
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped);
        var harness = CreateHarness(service, isKnownDown: false);

        var context = await InvokeAsync(harness, MonitorPath);

        Assert.Equal(expected: 1, harness.NextCalls);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task AMonitorRequestWaitsForTheRestartThenRuns()
    {
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped) { OnStart = database.BeginAnswering };
        var harness = CreateHarness(service, database);

        var context = await InvokeAsync(harness, MonitorPath);

        Assert.Equal(expected: 1, service.StartCount);
        Assert.Equal(expected: 1, harness.NextCalls);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task AFailedRestartGivesA503WithThePlainReportAndSkipsTheRequest()
    {
        var service = new RecoveryFakeServiceControl(MongoServiceState.NotInstalled);
        var harness = CreateHarness(service);

        var context = await InvokeAsync(harness, MonitorPath);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.StartsWith(JsonContentType, context.Response.ContentType, StringComparison.Ordinal);
        Assert.Equal(expected: 0, harness.NextCalls);
        string error = await ReadErrorAsync(context);
        Assert.Contains(NotInstalledProbe, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedMonitorRequestIsLoggedOnceAsAWarningNamingThePathAndEndpoint()
    {
        var service = new RecoveryFakeServiceControl(MongoServiceState.NotInstalled);
        var harness = CreateHarness(service);

        await InvokeAsync(harness, MonitorPath);

        string warning = Assert.Single(harness.Logger.Messages(LogLevel.Warning));
        Assert.Contains(MonitorPath, warning, StringComparison.Ordinal);
        Assert.Contains(Endpoint, warning, StringComparison.Ordinal);
        Assert.Equal(expected: 0, harness.Logger.Count(LogLevel.Error));
    }

    [Fact]
    public async Task ADownstreamDatabaseOutageGivesA503WithItsMessage()
    {
        var unavailable = new DatabaseUnavailableException(Endpoint, new IOException("refused"));
        var harness = CreateHarness(new RecoveryFakeServiceControl(MongoServiceState.Stopped),
                                    isKnownDown: false,
                                    nextFailure: unavailable
                                   );

        var context = await InvokeAsync(harness, MonitorPath);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal(unavailable.Message, await ReadErrorAsync(context));
        Assert.Equal(expected: 1, harness.Logger.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task AnUnrelatedDownstreamFailurePropagatesUnchanged()
    {
        var failure = new InvalidOperationException("unrelated");
        var harness = CreateHarness(new RecoveryFakeServiceControl(MongoServiceState.Stopped),
                                    isKnownDown: false,
                                    nextFailure: failure
                                   );

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(harness, MonitorPath));

        Assert.Same(failure, thrown);
        Assert.Equal(expected: 0, harness.Logger.Count(LogLevel.Warning));
    }

    private static async Task<DefaultHttpContext> InvokeAsync(Harness harness, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        await harness.Middleware.InvokeAsync(context, harness.Recovery, harness.Factory, harness.Logger);
        return context;
    }

    private static async Task<string> ReadErrorAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body,
                                                           cancellationToken: TestContext.Current.CancellationToken
                                                          );
        string error = document.RootElement.GetProperty(ErrorProperty).GetString() ?? string.Empty;
        return error;
    }

    private static Harness CreateHarness(RecoveryFakeServiceControl service,
                                         RecoveryFakeDatabase? database = null,
                                         bool isKnownDown = true,
                                         Exception? nextFailure = null)
    {
        IReadOnlyList<IRecoverableDatabase> databases =
            [database ?? new RecoveryFakeDatabase(isLocal: true, isKnownDown)];
        var recovery = new DatabaseRecovery(() => databases,
                                            service,
                                            Options.Create(new SaddleRagDbSettings()),
                                            TimeProvider.System,
                                            new RecordingLogger<DatabaseRecovery>(),
                                            RecoveryTestTimings.Short
                                           );
        var factory = new SaddleRagDbContextFactory(Options.Create(new SaddleRagDbSettings()));
        var harness = new Harness(recovery, factory, nextFailure);
        return harness;
    }

    private const string MonitorPath = "/api/monitor/docling-launch-request";
    private const string McpPath = "/mcp";
    private const string Endpoint = "localhost:27017";
    private const string JsonContentType = "application/json";
    private const string ErrorProperty = "error";
    private const string NotInstalledProbe = "is not installed";

    /// <summary>The middleware under test with the collaborators it needs and a counting next step.</summary>
    private sealed class Harness
    {
        public Harness(DatabaseRecovery recovery, SaddleRagDbContextFactory factory, Exception? nextFailure)
        {
            Recovery = recovery;
            Factory = factory;
            Middleware = new MonitorDatabaseRecoveryMiddleware(_ =>
                                                               {
                                                                   Interlocked.Increment(ref mNextCalls);
                                                                   if (nextFailure != null)
                                                                       throw nextFailure;
                                                                   return Task.CompletedTask;
                                                               }
                                                              );
        }

        private int mNextCalls;

        public DatabaseRecovery Recovery { get; }

        public SaddleRagDbContextFactory Factory { get; }

        public MonitorDatabaseRecoveryMiddleware Middleware { get; }

        public RecordingLogger<MonitorDatabaseRecoveryMiddleware> Logger { get; } = new();

        public int NextCalls => Volatile.Read(ref mNextCalls);
    }
}
