// ComponentStatusProbeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

public sealed class ComponentStatusProbeTests
{
    private static readonly Uri smSaddleRagEndpoint = new("http://localhost:6100");
    private static readonly Uri smDoclingEndpoint = new("http://localhost:5001");
    private static readonly Uri smOllamaEndpoint = new("http://localhost:11434");

    private sealed class StubHandler : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        {
            mRespond = respond;
        }

        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> mRespond;

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return mRespond(request);
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                     CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class FakePortOwnerLookup : IPortOwnerLookup
    {
        public FakePortOwnerLookup(bool listening)
        {
            mListening = listening;
        }

        private readonly bool mListening;

        public bool ThrowOnRead { get; set; }

        public List<int> Lookups { get; } = [];

        public ListeningProcess? FindListeningProcess(int port)
        {
            Lookups.Add(port);
            if (ThrowOnRead)
                throw new Win32Exception(1, "the listener table could not be read");

            return mListening ? new ListeningProcess(ProcessId: 1234, "listener", ExecutablePath: null) : null;
        }
    }

    private sealed class FakeServiceReader : IServiceStateReader
    {
        public FakeServiceReader(McpServiceState state)
        {
            mState = state;
        }

        private readonly McpServiceState mState;

        public bool ThrowOnRead { get; set; }

        public List<string> Reads { get; } = [];

        public McpServiceState GetState(string serviceName)
        {
            Reads.Add(serviceName);
            if (ThrowOnRead)
                throw new InvalidOperationException("the service control manager is unavailable");

            return mState;
        }
    }

    private sealed class CapturingLogger : ILogger<ComponentStatusProbe>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel,
                                EventId eventId,
                                TState state,
                                Exception? exception,
                                Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }

    /// <summary>A clock whose timers fire only when the test says so, so the 3 s limit costs no wall-clock time.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        public List<TimeSpan> DueTimes { get; } = [];

        private readonly List<ManualTimer> mTimers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTimes.Add(dueTime);
            var timer = new ManualTimer(callback, state);
            mTimers.Add(timer);
            return timer;
        }

        public void FireAll()
        {
            foreach (ManualTimer timer in mTimers.ToArray())
                timer.Fire();
        }

        private sealed class ManualTimer : ITimer
        {
            public ManualTimer(TimerCallback callback, object? state)
            {
                mCallback = callback;
                mState = state;
            }

            private readonly TimerCallback mCallback;
            private readonly object? mState;

            public void Fire() => mCallback(mState);

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static ComponentDefinition SaddleRag(string? serviceName = "SaddleRAGMcp") =>
        new(ComponentKind.SaddleRagService, "SaddleRAG service", smSaddleRagEndpoint, Port: 6100,
            HostedLocally: true, serviceName, ExecutablePath: null, ModelsDirectory: null);

    private static ComponentDefinition Mongo(int? port, string? serviceName = "MongoDB") =>
        new(ComponentKind.MongoDb, "MongoDB", Endpoint: null, port,
            HostedLocally: true, serviceName, ExecutablePath: null, ModelsDirectory: null);

    private static ComponentDefinition Docling(bool hostedLocally = true) =>
        new(ComponentKind.Docling, "Docling", smDoclingEndpoint, Port: 5001,
            hostedLocally, WindowsServiceName: null, ExecutablePath: null, ModelsDirectory: null);

    private static ComponentDefinition Ollama() =>
        new(ComponentKind.Ollama, "Ollama", smOllamaEndpoint, Port: 11434,
            HostedLocally: true, WindowsServiceName: null, ExecutablePath: null, ModelsDirectory: null);

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<HttpResponseMessage> Respond(HttpResponseMessage response) => Task.FromResult(response);

    private static Task<HttpResponseMessage> Refused(HttpRequestMessage request) =>
        throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made");

    private static Task<HttpResponseMessage> MustNotBeCalled(HttpRequestMessage request) =>
        throw new InvalidOperationException("this probe must not make an HTTP request");

    private static ComponentStatusProbe MakeProbe(HttpMessageHandler handler,
                                                  IServiceStateReader services,
                                                  TimeProvider? time = null,
                                                  IPortOwnerLookup? ports = null,
                                                  ILogger<ComponentStatusProbe>? logger = null) =>
        new(new HttpClient(handler), services, time, ports, logger);

    private static FakePortOwnerLookup NothingListening() => new(listening: false);

    private static int FindClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        int port = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task StoppedMongoServiceIsStopped()
    {
        var services = new FakeServiceReader(McpServiceState.Stopped);
        FakePortOwnerLookup ports = NothingListening();
        ComponentStatusProbe probe = MakeProbe(new StubHandler(MustNotBeCalled), services, ports: ports);

        ComponentStatus status = await probe.ProbeAsync(Mongo(FindClosedPort()), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Stopped, status.State);
        Assert.Equal(ComponentKind.MongoDb, status.Kind);
        Assert.Empty(ports.Lookups);
        Assert.Equal(["MongoDB"], services.Reads);
    }

    [Fact]
    public async Task RunningMongoServiceWhosePortRefusesIsUnreachable()
    {
        ComponentStatusProbe probe = MakeProbe(new StubHandler(MustNotBeCalled), new FakeServiceReader(McpServiceState.Running), ports: NothingListening());

        ComponentStatus status = await probe.ProbeAsync(Mongo(FindClosedPort()), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Unreachable, status.State);
    }

    [Fact]
    public async Task RunningMongoServiceWhosePortRefusesARealConnectionIsUnreachable()
    {
        // No listener table: the probe must find the refusal by connecting, within its time limit.
        ComponentStatusProbe probe = MakeProbe(new StubHandler(MustNotBeCalled), new FakeServiceReader(McpServiceState.Running));

        ComponentStatus status = await probe.ProbeAsync(Mongo(FindClosedPort()), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Unreachable, status.State);
        Assert.Contains("refused", status.Detail);
    }

    [Fact]
    public async Task MongoPortWithNoListenerIsNotConnectedTo()
    {
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint) listener.LocalEndpoint).Port;
            FakePortOwnerLookup ports = NothingListening();
            ComponentStatusProbe probe = MakeProbe(new StubHandler(MustNotBeCalled), new FakeServiceReader(McpServiceState.Running), ports: ports);

            // The port really is open, but the listener table said otherwise, so no connection is tried.
            ComponentStatus status = await probe.ProbeAsync(Mongo(port), TestContext.Current.CancellationToken);

            Assert.Equal(ComponentState.Unreachable, status.State);
            Assert.Equal([port], ports.Lookups);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RunningMongoServiceWithAnOpenPortIsRunning()
    {
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint) listener.LocalEndpoint).Port;
            ComponentStatusProbe probe = MakeProbe(new StubHandler(MustNotBeCalled), new FakeServiceReader(McpServiceState.Running));

            ComponentStatus status = await probe.ProbeAsync(Mongo(port), TestContext.Current.CancellationToken);

            Assert.Equal(ComponentState.Running, status.State);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task MongoWithNoServiceAndNothingOnThePortIsStopped()
    {
        var services = new FakeServiceReader(McpServiceState.Running);
        ComponentStatusProbe probe = MakeProbe(new StubHandler(MustNotBeCalled), services, ports: NothingListening());

        ComponentStatus status = await probe.ProbeAsync(Mongo(FindClosedPort(), serviceName: null), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Stopped, status.State);
        Assert.Empty(services.Reads);
    }

    [Fact]
    public async Task MongoServiceThatIsNotInstalledIsNotInstalled()
    {
        ComponentStatusProbe probe = MakeProbe(new StubHandler(MustNotBeCalled), new FakeServiceReader(McpServiceState.NotInstalled));

        ComponentStatus status = await probe.ProbeAsync(Mongo(FindClosedPort()), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.NotInstalled, status.State);
    }

    [Fact]
    public async Task ServiceThatIsChangingStateIsUnknown()
    {
        ComponentStatusProbe probe = MakeProbe(new StubHandler(MustNotBeCalled), new FakeServiceReader(McpServiceState.Transitioning));

        ComponentStatus status = await probe.ProbeAsync(Mongo(FindClosedPort()), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Unknown, status.State);
    }

    [Fact]
    public async Task DoclingThatIsStartingIsUnreachable()
    {
        var handler = new StubHandler(_ => Respond(Json(HttpStatusCode.OK, """{"status":"starting"}""")));
        ComponentStatusProbe probe = MakeProbe(handler, new FakeServiceReader(McpServiceState.Running));

        ComponentStatus status = await probe.ProbeAsync(Docling(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Unreachable, status.State);
        Assert.Contains("starting", status.Detail);
        Assert.Equal(new Uri(smDoclingEndpoint, "/health"), Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task DoclingReportingOkIsRunning()
    {
        var handler = new StubHandler(_ => Respond(Json(HttpStatusCode.OK, """{"status":"ok"}""")));
        ComponentStatusProbe probe = MakeProbe(handler, new FakeServiceReader(McpServiceState.Running));

        ComponentStatus status = await probe.ProbeAsync(Docling(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Running, status.State);
    }

    [Theory]
    [InlineData("""{"status":5}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""not json""")]
    public async Task DoclingHealthBodyThatIsNotAnOkReportIsUnreachable(string body)
    {
        var handler = new StubHandler(_ => Respond(Json(HttpStatusCode.OK, body)));
        ComponentStatusProbe probe = MakeProbe(handler, new FakeServiceReader(McpServiceState.Running));

        ComponentStatus status = await probe.ProbeAsync(Docling(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Unreachable, status.State);
    }

    [Fact]
    public async Task DoclingConnectionRefusalIsStopped()
    {
        ComponentStatusProbe probe = MakeProbe(new StubHandler(Refused), new FakeServiceReader(McpServiceState.Running));

        ComponentStatus status = await probe.ProbeAsync(Docling(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Stopped, status.State);
    }

    [Fact]
    public async Task OllamaConnectionRefusalIsStopped()
    {
        ComponentStatusProbe probe = MakeProbe(new StubHandler(Refused), new FakeServiceReader(McpServiceState.Running));

        ComponentStatus status = await probe.ProbeAsync(Ollama(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Stopped, status.State);
        Assert.Equal(ComponentKind.Ollama, status.Kind);
    }

    [Fact]
    public async Task OllamaRealConnectionRefusalWithoutAListenerTableIsStopped()
    {
        using var client = new HttpClient();
        var probe = new ComponentStatusProbe(client, new FakeServiceReader(McpServiceState.Running));
        ComponentDefinition ollama = Ollama() with { Endpoint = new Uri($"http://127.0.0.1:{FindClosedPort()}") };

        ComponentStatus status = await probe.ProbeAsync(ollama, TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Stopped, status.State);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task OllamaOnAClosedPortIsStoppedQuicklyUsingTheRealListenerTable()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("The listener table exists only on Windows.");

        using var client = new HttpClient();
        var probe = new ComponentStatusProbe(client, new FakeServiceReader(McpServiceState.Running), ports: new TcpTablePortOwnerLookup());
        ComponentDefinition ollama = Ollama() with { Endpoint = new Uri($"http://localhost:{FindClosedPort()}") };
        long start = Environment.TickCount64;

        ComponentStatus status = await probe.ProbeAsync(ollama, TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Stopped, status.State);
        Assert.True(Environment.TickCount64 - start < 1500, "a closed port must not wait for a connection to be refused");
    }

    [Fact]
    public async Task OllamaPortWithNoListenerIsStoppedWithoutAskingOllama()
    {
        ComponentStatusProbe probe = MakeProbe(new StubHandler(MustNotBeCalled), new FakeServiceReader(McpServiceState.Running), ports: NothingListening());

        ComponentStatus status = await probe.ProbeAsync(Ollama(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Stopped, status.State);
    }

    [Fact]
    public async Task OllamaPortWithAListenerIsAskedForItsVersion()
    {
        var handler = new StubHandler(_ => Respond(Json(HttpStatusCode.OK, """{"version":"0.1.0"}""")));
        ComponentStatusProbe probe = MakeProbe(handler, new FakeServiceReader(McpServiceState.Running), ports: new FakePortOwnerLookup(listening: true));

        ComponentStatus status = await probe.ProbeAsync(Ollama(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Running, status.State);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AListenerTableThatCannotBeReadFallsBackToTheRequest()
    {
        var handler = new StubHandler(_ => Respond(Json(HttpStatusCode.OK, """{"version":"0.1.0"}""")));
        var ports = new FakePortOwnerLookup(listening: false) { ThrowOnRead = true };
        ComponentStatusProbe probe = MakeProbe(handler, new FakeServiceReader(McpServiceState.Running), ports: ports);

        ComponentStatus status = await probe.ProbeAsync(Ollama(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Running, status.State);
    }

    [Fact]
    public async Task OllamaAnswering200IsRunning()
    {
        var handler = new StubHandler(_ => Respond(Json(HttpStatusCode.OK, """{"version":"0.1.0"}""")));
        ComponentStatusProbe probe = MakeProbe(handler, new FakeServiceReader(McpServiceState.Running));

        ComponentStatus status = await probe.ProbeAsync(Ollama(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Running, status.State);
        Assert.Equal(new Uri(smOllamaEndpoint, "/api/version"), Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task OllamaAnsweringAnythingButOkIsUnreachable()
    {
        var handler = new StubHandler(_ => Respond(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        ComponentStatusProbe probe = MakeProbe(handler, new FakeServiceReader(McpServiceState.Running));

        ComponentStatus status = await probe.ProbeAsync(Ollama(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Unreachable, status.State);
    }

    [Fact]
    public async Task SaddleRagServiceAnswering503WithJsonIsRunning()
    {
        var handler = new StubHandler(_ => Respond(Json(HttpStatusCode.ServiceUnavailable, """{"status":"Unavailable"}""")));
        ComponentStatusProbe probe = MakeProbe(handler, new FakeServiceReader(McpServiceState.Running));

        ComponentStatus status = await probe.ProbeAsync(SaddleRag(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Running, status.State);
        Assert.Contains("Unavailable", status.Detail);
    }

    [Fact]
    public async Task SaddleRagServiceAnsweringJsonWithoutAStatusIsRunning()
    {
        var handler = new StubHandler(_ => Respond(Json(HttpStatusCode.OK, """{"services":[]}""")));
        ComponentStatusProbe probe = MakeProbe(handler, new FakeServiceReader(McpServiceState.Running));

        ComponentStatus status = await probe.ProbeAsync(SaddleRag(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Running, status.State);
    }

    [Fact]
    public async Task SaddleRagServiceAnsweringNonJsonIsUnreachable()
    {
        var handler = new StubHandler(_ => Respond(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html/>") }));
        ComponentStatusProbe probe = MakeProbe(handler, new FakeServiceReader(McpServiceState.Running));

        ComponentStatus status = await probe.ProbeAsync(SaddleRag(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Unreachable, status.State);
    }

    [Fact]
    public async Task RunningSaddleRagServiceWhoseHealthIsUnreachableIsStillReported()
    {
        ComponentStatusProbe probe = MakeProbe(new StubHandler(Refused), new FakeServiceReader(McpServiceState.Running));

        ComponentStatus status = await probe.ProbeAsync(SaddleRag(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Unreachable, status.State);
        Assert.Equal(ComponentKind.SaddleRagService, status.Kind);
    }

    [Fact]
    public async Task StoppedSaddleRagServiceIsStoppedWithoutAskingHealth()
    {
        ComponentStatusProbe probe = MakeProbe(new StubHandler(MustNotBeCalled), new FakeServiceReader(McpServiceState.Stopped));

        ComponentStatus status = await probe.ProbeAsync(SaddleRag(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Stopped, status.State);
    }

    [Fact]
    public async Task SaddleRagServiceThatIsNotInstalledIsNotInstalled()
    {
        ComponentStatusProbe probe = MakeProbe(new StubHandler(MustNotBeCalled), new FakeServiceReader(McpServiceState.NotInstalled));

        ComponentStatus status = await probe.ProbeAsync(SaddleRag(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.NotInstalled, status.State);
    }

    [Fact]
    public async Task MongoStatusNeedsNoHealthEndpoint()
    {
        // The window must show MongoDB even when the SaddleRAG service is down, so its probe never
        // asks the service anything.
        var handler = new StubHandler(MustNotBeCalled);
        ComponentStatusProbe probe = MakeProbe(handler, new FakeServiceReader(McpServiceState.Running));

        ComponentStatus status = await probe.ProbeAsync(Mongo(FindClosedPort()), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Unreachable, status.State);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(ComponentKind.SaddleRagService)]
    [InlineData(ComponentKind.MongoDb)]
    [InlineData(ComponentKind.Docling)]
    [InlineData(ComponentKind.Ollama)]
    public async Task ARemoteRowIsUnknownAndNeverContacted(ComponentKind kind)
    {
        var services = new FakeServiceReader(McpServiceState.Running);
        var handler = new StubHandler(MustNotBeCalled);
        ComponentStatusProbe probe = MakeProbe(handler, services);
        var remote = new ComponentDefinition(kind, "Remote", new Uri("http://example.invalid:5001"), Port: 27017,
                                             HostedLocally: false, WindowsServiceName: "SomeService",
                                             ExecutablePath: null, ModelsDirectory: null);

        ComponentStatus status = await probe.ProbeAsync(remote, TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Unknown, status.State);
        Assert.Equal(kind, status.Kind);
        Assert.Equal("Hosted on another computer; see the SaddleRAG health page.", status.Detail);
        Assert.Empty(handler.Requests);
        Assert.Empty(services.Reads);
    }

    [Fact]
    public async Task AProbeThatTakesLongerThanThreeSecondsIsUnreachable()
    {
        var handler = new HangingHandler();
        var time = new ManualTimeProvider();
        ComponentStatusProbe probe = MakeProbe(handler, new FakeServiceReader(McpServiceState.Running), time);

        Task<ComponentStatus> pending = probe.ProbeAsync(Ollama(), TestContext.Current.CancellationToken);
        await handler.Started.Task;
        time.FireAll();
        ComponentStatus status = await pending;

        Assert.Equal(TimeSpan.FromSeconds(seconds: 3), Assert.Single(time.DueTimes));
        Assert.Equal(ComponentState.Unreachable, status.State);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        ComponentStatusProbe probe = MakeProbe(new StubHandler(MustNotBeCalled), new FakeServiceReader(McpServiceState.Running));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync(Ollama(), cts.Token));
    }

    [Fact]
    public async Task AnUnexpectedFailureIsUnknownAndLoggedOncePerMessage()
    {
        var services = new FakeServiceReader(McpServiceState.Running) { ThrowOnRead = true };
        var logger = new CapturingLogger();
        ComponentStatusProbe probe = MakeProbe(new StubHandler(MustNotBeCalled), services, logger: logger);

        ComponentStatus first = await probe.ProbeAsync(SaddleRag(), TestContext.Current.CancellationToken);
        ComponentStatus second = await probe.ProbeAsync(SaddleRag(), TestContext.Current.CancellationToken);

        Assert.Equal(ComponentState.Unknown, first.State);
        Assert.Equal(ComponentState.Unknown, second.State);
        Assert.Contains("service control manager", first.Detail);
        Assert.Equal([LogLevel.Warning], logger.Levels);
    }

    [Fact]
    public async Task OrdinaryProbeOutcomesAreNotLogged()
    {
        var logger = new CapturingLogger();
        ComponentStatusProbe probe = MakeProbe(new StubHandler(Refused), new FakeServiceReader(McpServiceState.Running), logger: logger);

        await probe.ProbeAsync(Ollama(), TestContext.Current.CancellationToken);
        await probe.ProbeAsync(SaddleRag(), TestContext.Current.CancellationToken);

        Assert.Empty(logger.Levels);
    }
}