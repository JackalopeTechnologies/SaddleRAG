// ComponentControllerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

/// <summary>
///     Every seam is a fake: nothing here starts, stops or elevates a real process. The clock is a
///     stepping fake, so the 15-second and 60-second waits cost no wall-clock time.
/// </summary>
public sealed class ComponentControllerTests
{
    public ComponentControllerTests()
    {
        mHandler = new StubHandler(this);
        mHttp = new HttpClient(mHandler);
        mPorts = new FakePorts(this);
        mProbe = new ComponentStatusProbe(mHttp, Substitute.For<IServiceStateReader>(), time: null, mPorts);
        mServices = new FakeServiceRunner(this);
        mStopper = new FakeStopper(this);
        mDocling = new FakeDoclingLauncher(this);
        mStarter = new FakeStarter(this);
        mConfirmation = new FakeConfirmation(this);
        mLogger = new RecordingLogger();
        mTime = new SteppingTimeProvider();
        mController = new ComponentController(mServices,
                                              mProbe,
                                              mPorts,
                                              mStopper,
                                              mDocling,
                                              mStarter,
                                              mConfirmation,
                                              mLogger,
                                              mTime);
    }

    private const int DoclingPort = 5001;
    private const int OllamaPort = 11434;

    private static readonly string smOllamaExe = Path.Combine(Path.GetTempPath(), "ollama", "ollama.exe");
    private static readonly string smOllamaModels = Path.Combine(Path.GetTempPath(), "ollama", "models");

    private readonly List<string> mEvents = [];
    private readonly StubHandler mHandler;
    private readonly HttpClient mHttp;
    private readonly FakePorts mPorts;
    private readonly ComponentStatusProbe mProbe;
    private readonly FakeServiceRunner mServices;
    private readonly FakeStopper mStopper;
    private readonly FakeDoclingLauncher mDocling;
    private readonly FakeStarter mStarter;
    private readonly FakeConfirmation mConfirmation;
    private readonly RecordingLogger mLogger;
    private readonly SteppingTimeProvider mTime;
    private readonly ComponentController mController;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region Rows

    private static ComponentDefinition SaddleRag() =>
        new(ComponentKind.SaddleRagService, "SaddleRAG", new Uri("http://localhost:6100"), Port: 6100,
            HostedLocally: true, "SaddleRAGMcp", ExecutablePath: null, ModelsDirectory: null);

    private static ComponentDefinition Mongo(string? serviceName = "MongoDB", bool local = true) =>
        new(ComponentKind.MongoDb, "MongoDB", local ? new Uri("mongodb://localhost:27017") : null, local ? 27017 : null,
            local, serviceName, ExecutablePath: null, ModelsDirectory: null);

    private static ComponentDefinition Docling(bool local = true) =>
        new(ComponentKind.Docling, "Docling", new Uri(local ? "http://localhost:5001" : "http://docs.example.com:5001"),
            DoclingPort, local, WindowsServiceName: null, ExecutablePath: null, ModelsDirectory: null);

    private static ComponentDefinition Ollama(string? executable = null, string? models = null, bool local = true) =>
        new(ComponentKind.Ollama, "Ollama", new Uri(local ? "http://localhost:11434" : "http://gpu.example.com:11434"),
            OllamaPort, local, WindowsServiceName: null, executable ?? smOllamaExe, models ?? smOllamaModels);

    private static ListeningProcess Owner(int pid, string name) =>
        new(pid, name, ExecutablePath: null, new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc));

    private static IReadOnlyList<ComponentDefinition> AllLocal() => [SaddleRag(), Mongo(), Docling(), Ollama()];

    #endregion

    #region Fakes

    private sealed class StubHandler : HttpMessageHandler
    {
        public StubHandler(ComponentControllerTests owner)
        {
            mOwner = owner;
        }

        private readonly ComponentControllerTests mOwner;

        public bool Healthy { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken cancellationToken)
        {
            mOwner.mEvents.Add($"http {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(new HttpResponseMessage(Healthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class FakePorts : IPortOwnerLookup
    {
        public FakePorts(ComponentControllerTests owner)
        {
            mOwner = owner;
        }

        private readonly ComponentControllerTests mOwner;

        public Dictionary<int, ListeningProcess?> Listeners { get; } = [];

        public bool ThrowOnRead { get; set; }

        public int Lookups { get; private set; }

        public ListeningProcess? FindListeningProcess(int port)
        {
            Lookups++;
            mOwner.mEvents.Add($"lookup {port}");
            if (ThrowOnRead)
                throw new Win32Exception(5, "the listener table could not be read");

            return Listeners.GetValueOrDefault(port);
        }
    }

    private sealed class FakeServiceRunner : IServiceActionRunner
    {
        public FakeServiceRunner(ComponentControllerTests owner)
        {
            mOwner = owner;
        }

        private readonly ComponentControllerTests mOwner;

        public ServiceActionOutcome Outcome { get; set; } = ServiceActionOutcome.Succeeded;

        public TaskCompletionSource<ServiceActionOutcome>? Gate { get; set; }

        public List<(IReadOnlyList<string> Names, ServiceAction Action)> Calls { get; } = [];

        public async Task<ServiceActionOutcome> RunAsync(IReadOnlyList<string> serviceNames,
                                                         ServiceAction action,
                                                         CancellationToken ct)
        {
            Calls.Add((serviceNames.ToList(), action));
            mOwner.mEvents.Add($"elevated {action} {string.Join(",", serviceNames)}");
            ServiceActionOutcome result = Gate is null ? Outcome : await Gate.Task;
            return result;
        }
    }

    private sealed class FakeStopper : IUserProcessStopper
    {
        public FakeStopper(ComponentControllerTests owner)
        {
            mOwner = owner;
        }

        private readonly ComponentControllerTests mOwner;

        public ProcessStopOutcome Outcome { get; set; } = ProcessStopOutcome.Stopped;

        /// <summary>What listens on the port once the stop returns; null frees it.</summary>
        public Action? OnStop { get; set; }

        public List<(ListeningProcess Process, bool Tree)> Calls { get; } = [];

        public Task<ProcessStopOutcome> StopAsync(ListeningProcess process,
                                                  bool entireProcessTree,
                                                  TimeSpan timeout,
                                                  CancellationToken ct)
        {
            Calls.Add((process, entireProcessTree));
            mOwner.mEvents.Add($"kill {process.ProcessName} {process.ProcessId}");
            OnStop?.Invoke();
            return Task.FromResult(Outcome);
        }
    }

    private sealed class FakeDoclingLauncher : IDoclingLauncher
    {
        public FakeDoclingLauncher(ComponentControllerTests owner)
        {
            mOwner = owner;
        }

        private readonly ComponentControllerTests mOwner;

        public DoclingLaunchOutcome Outcome { get; set; } = DoclingLaunchOutcome.Ready;

        public int EnsureCalls { get; private set; }

        public int AllowCalls { get; private set; }

        public bool LaunchedProcessIsRunning { get; set; }

        public Task<DoclingLaunchOutcome> EnsureRunningAsync(CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            mOwner.mEvents.Add("docling ensure");
            return Task.FromResult(Outcome);
        }

        public void AllowRelaunchAfterOperatorStop()
        {
            AllowCalls++;
            mOwner.mEvents.Add("docling allow");
        }
    }

    private sealed class FakeStarter : IProcessStarter
    {
        public FakeStarter(ComponentControllerTests owner)
        {
            mOwner = owner;
        }

        private readonly ComponentControllerTests mOwner;

        public List<ProcessStartInfo> Started { get; } = [];

        public Action? OnStart { get; set; }

        public IDisposable? Start(ProcessStartInfo startInfo)
        {
            Started.Add(startInfo);
            mOwner.mEvents.Add($"start {startInfo.FileName} {startInfo.Arguments}");
            OnStart?.Invoke();
            return null;
        }
    }

    private sealed class FakeConfirmation : IOperatorConfirmation
    {
        public FakeConfirmation(ComponentControllerTests owner)
        {
            mOwner = owner;
        }

        private readonly ComponentControllerTests mOwner;

        public bool Answer { get; set; } = true;

        public List<(ComponentDefinition Component, ListeningProcess Process, bool LooksExpected)> Calls { get; } = [];

        public Task<bool> ConfirmStopAsync(ComponentDefinition component, ListeningProcess process, bool looksExpected)
        {
            Calls.Add((component, process, looksExpected));
            mOwner.mEvents.Add($"confirm {process.ProcessName}");
            return Task.FromResult(Answer);
        }
    }

    private sealed class RecordingLogger : ILogger<ComponentController>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel,
                                EventId eventId,
                                TState state,
                                Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            lock(Entries)
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>
    ///     A clock that moves forward by each timer's due time and fires the timer at once, so a
    ///     bounded wait runs to its deadline instantly.
    /// </summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private readonly Lock mSync = new();
        private DateTimeOffset mNow = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Start { get; } = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock(mSync)
                return mNow;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                lock(mSync)
                    mNow += dueTime;

                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }

            return new NoOpTimer();
        }

        private sealed class NoOpTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    #endregion

    #region Remote rows and the busy guard

    [Fact]
    public async Task ARemoteRowIsNotControllableAndNoSeamIsCalled()
    {
        ComponentDefinition[] remote = [Mongo(local: false), Docling(local: false), Ollama(local: false)];

        foreach (ComponentDefinition row in remote)
        {
            Assert.Equal(ComponentActionStatus.NotControllable, (await mController.StartAsync(row, Ct)).Status);
            Assert.Equal(ComponentActionStatus.NotControllable, (await mController.StopAsync(row, Ct)).Status);
            Assert.Equal(ComponentActionStatus.NotControllable, (await mController.RestartAsync(row, Ct)).Status);
        }

        Assert.Empty(mEvents);
    }

    [Fact]
    public async Task AnActionWhileAnotherIsRunningReturnsBusyAtOnce()
    {
        mServices.Gate = new TaskCompletionSource<ServiceActionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ComponentActionResult> first = mController.RestartAsync(SaddleRag(), Ct);

        ComponentActionResult second = await mController.StartAsync(Mongo(), Ct);
        IReadOnlyList<ComponentActionResult> all = await mController.RestartAllAsync(AllLocal(), Ct);

        Assert.Equal(ComponentActionStatus.Busy, second.Status);
        ComponentActionResult busy = Assert.Single(all);
        Assert.Equal(ComponentActionStatus.Busy, busy.Status);
        Assert.Single(mServices.Calls);

        mServices.Gate.SetResult(ServiceActionOutcome.Succeeded);
        Assert.Equal(ComponentActionStatus.Succeeded, (await first).Status);
        Assert.Equal(ComponentActionStatus.Succeeded, (await mController.StartAsync(Mongo(), Ct)).Status);
    }

    #endregion

    #region Windows-service components

    [Theory]
    [InlineData(ServiceActionOutcome.Succeeded, ComponentActionStatus.Succeeded)]
    [InlineData(ServiceActionOutcome.Cancelled, ComponentActionStatus.Cancelled)]
    [InlineData(ServiceActionOutcome.TimedOut, ComponentActionStatus.TimedOut)]
    [InlineData(ServiceActionOutcome.UnsafeInstallLocation, ComponentActionStatus.Denied)]
    [InlineData(ServiceActionOutcome.NotAllowed, ComponentActionStatus.Failed)]
    [InlineData(ServiceActionOutcome.NotInstalled, ComponentActionStatus.Failed)]
    [InlineData(ServiceActionOutcome.ScriptMissing, ComponentActionStatus.Failed)]
    [InlineData(ServiceActionOutcome.Failed, ComponentActionStatus.Failed)]
    public async Task AServiceOutcomeMapsToAnActionStatusWithTheOutcomeText(ServiceActionOutcome outcome,
                                                                            ComponentActionStatus expected)
    {
        mServices.Outcome = outcome;

        ComponentActionResult result = await mController.RestartAsync(Mongo(), Ct);

        Assert.Equal(expected, result.Status);
        Assert.Equal(ComponentKind.MongoDb, result.Kind);
        Assert.Equal(ServiceActionOutcomeText.Describe(outcome, ServiceAction.Restart, "MongoDB"), result.Message);
        (IReadOnlyList<string> names, ServiceAction action) = Assert.Single(mServices.Calls);
        Assert.Equal(["MongoDB"], names);
        Assert.Equal(ServiceAction.Restart, action);
    }

    [Fact]
    public async Task StartAndStopPassTheirOwnActionToTheServiceRunner()
    {
        await mController.StartAsync(SaddleRag(), Ct);
        await mController.StopAsync(SaddleRag(), Ct);

        Assert.Equal([ServiceAction.Start, ServiceAction.Stop], mServices.Calls.Select(call => call.Action));
        Assert.All(mServices.Calls, call => Assert.Equal(["SaddleRAGMcp"], call.Names));
    }

    [Fact]
    public async Task ALocalMongoWithNoServiceIsNotControllable()
    {
        ComponentActionResult result = await mController.RestartAsync(Mongo(serviceName: null), Ct);

        Assert.Equal(ComponentActionStatus.NotControllable, result.Status);
        Assert.Equal("MongoDB is not running as a Windows service on this computer.", result.Message);
        Assert.Empty(mServices.Calls);
    }

    #endregion

    #region Stopping Docling and Ollama

    [Fact]
    public async Task StoppingWithNoListenerIsAlreadyInStateAndAsksNothing()
    {
        ComponentActionResult result = await mController.StopAsync(Docling(), Ct);

        Assert.Equal(ComponentActionStatus.AlreadyInState, result.Status);
        Assert.Empty(mConfirmation.Calls);
        Assert.Empty(mStopper.Calls);
    }

    [Theory]
    [InlineData(ComponentKind.Docling, "python", true)]
    [InlineData(ComponentKind.Docling, "Python", true)]
    [InlineData(ComponentKind.Docling, "docling-serve", true)]
    [InlineData(ComponentKind.Docling, "pwsh", false)]
    [InlineData(ComponentKind.Docling, "powershell", false)]
    [InlineData(ComponentKind.Docling, "ollama", false)]
    [InlineData(ComponentKind.Ollama, "ollama", true)]
    [InlineData(ComponentKind.Ollama, "ollama app", true)]
    [InlineData(ComponentKind.Ollama, "python", false)]
    public async Task TheOperatorIsAskedWithLooksExpectedAndOnlyAnExpectedOwnerLosesItsTree(ComponentKind kind,
                                                                                            string processName,
                                                                                            bool expected)
    {
        ComponentDefinition row = kind == ComponentKind.Docling ? Docling() : Ollama();
        int port = kind == ComponentKind.Docling ? DoclingPort : OllamaPort;
        mPorts.Listeners[port] = Owner(pid: 100, processName);
        mStopper.OnStop = () => mPorts.Listeners[port] = null;

        ComponentActionResult result = await mController.StopAsync(row, Ct);

        Assert.Equal(ComponentActionStatus.Succeeded, result.Status);
        (ComponentDefinition asked, ListeningProcess process, bool looksExpected) = Assert.Single(mConfirmation.Calls);
        Assert.Equal(row, asked);
        Assert.Equal(100, process.ProcessId);
        Assert.Equal(expected, looksExpected);
        Assert.Equal(expected, Assert.Single(mStopper.Calls).Tree);
    }

    [Fact]
    public async Task NotepadOnTheDoclingPortDoesNotLookExpected()
    {
        mPorts.Listeners[DoclingPort] = Owner(pid: 4242, "notepad");
        mConfirmation.Answer = false;

        await mController.StopAsync(Docling(), Ct);

        Assert.False(Assert.Single(mConfirmation.Calls).LooksExpected);
    }

    [Fact]
    public async Task DecliningTheConfirmationCancelsAndStopsNothing()
    {
        mPorts.Listeners[DoclingPort] = Owner(pid: 100, "python");
        mConfirmation.Answer = false;

        ComponentActionResult result = await mController.StopAsync(Docling(), Ct);

        Assert.Equal(ComponentActionStatus.Cancelled, result.Status);
        Assert.Empty(mStopper.Calls);
    }

    [Fact]
    public async Task AnOwnerUnderAnotherAccountIsDeniedByName()
    {
        mPorts.Listeners[OllamaPort] = Owner(pid: 321, "ollama");
        mStopper.Outcome = ProcessStopOutcome.AccessDenied;

        ComponentActionResult result = await mController.StopAsync(Ollama(), Ct);

        Assert.Equal(ComponentActionStatus.Denied, result.Status);
        Assert.Contains("ollama", result.Message, StringComparison.Ordinal);
        Assert.Contains("321", result.Message, StringComparison.Ordinal);
        Assert.Contains("another account", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProcessThatDoesNotExitIsTimedOut()
    {
        mPorts.Listeners[DoclingPort] = Owner(pid: 100, "python");
        mStopper.Outcome = ProcessStopOutcome.TimedOut;

        ComponentActionResult result = await mController.StopAsync(Docling(), Ct);

        Assert.Equal(ComponentActionStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task APortStillHeldBySameProcessAfterFifteenSecondsIsTimedOut()
    {
        mPorts.Listeners[DoclingPort] = Owner(pid: 100, "python");

        ComponentActionResult result = await mController.StopAsync(Docling(), Ct);

        Assert.Equal(ComponentActionStatus.TimedOut, result.Status);
        Assert.True(mTime.GetUtcNow() - mTime.Start >= TimeSpan.FromSeconds(15));
        Assert.True(mTime.GetUtcNow() - mTime.Start < TimeSpan.FromSeconds(16));
    }

    [Fact]
    public async Task ADifferentProcessIdOnThePortCountsAsStopped()
    {
        mPorts.Listeners[OllamaPort] = Owner(pid: 100, "ollama");
        mStopper.OnStop = () => mPorts.Listeners[OllamaPort] = Owner(pid: 200, "ollama");

        ComponentActionResult result = await mController.StopAsync(Ollama(), Ct);

        Assert.Equal(ComponentActionStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task AProcessThatVanishedBeforeTheStopWithThePortFreeIsAlreadyInState()
    {
        mPorts.Listeners[DoclingPort] = Owner(pid: 100, "python");
        mStopper.Outcome = ProcessStopOutcome.NotFound;
        mStopper.OnStop = () => mPorts.Listeners[DoclingPort] = null;

        ComponentActionResult result = await mController.StopAsync(Docling(), Ct);

        Assert.Equal(ComponentActionStatus.AlreadyInState, result.Status);
    }

    [Fact]
    public async Task AnUnreadableListenerTableFailsWithoutThrowing()
    {
        mPorts.ThrowOnRead = true;

        ComponentActionResult result = await mController.StopAsync(Docling(), Ct);

        Assert.Equal(ComponentActionStatus.Failed, result.Status);
        Assert.Contains("the listener table could not be read", result.Message, StringComparison.Ordinal);
    }

    #endregion

    #region Starting and restarting Docling

    [Theory]
    [InlineData(DoclingLaunchOutcome.Ready, ComponentActionStatus.Succeeded)]
    [InlineData(DoclingLaunchOutcome.AlreadyRunning, ComponentActionStatus.Succeeded)]
    [InlineData(DoclingLaunchOutcome.NotRegistered, ComponentActionStatus.Failed)]
    [InlineData(DoclingLaunchOutcome.Timeout, ComponentActionStatus.TimedOut)]
    [InlineData(DoclingLaunchOutcome.Failed, ComponentActionStatus.Failed)]
    public async Task ADoclingStartReportsTheLaunchOutcome(DoclingLaunchOutcome outcome, ComponentActionStatus expected)
    {
        mDocling.Outcome = outcome;

        ComponentActionResult result = await mController.StartAsync(Docling(), Ct);

        Assert.Equal(expected, result.Status);
        Assert.Equal(1, mDocling.EnsureCalls);
    }

    [Fact]
    public async Task StartingADoclingThatCrashedAfterLaunchAllowsARelaunchThenLaunches()
    {
        mDocling.LaunchedProcessIsRunning = false;

        await mController.StartAsync(Docling(), Ct);

        int allow = mEvents.IndexOf("docling allow");
        int ensure = mEvents.IndexOf("docling ensure");
        Assert.True(allow >= 0 && ensure > allow);
    }

    [Fact]
    public async Task StartingADoclingStillLoadingKeepsTheGuard()
    {
        mDocling.LaunchedProcessIsRunning = true;

        await mController.StartAsync(Docling(), Ct);

        Assert.Equal(0, mDocling.AllowCalls);
        Assert.Equal(1, mDocling.EnsureCalls);
    }

    [Fact]
    public async Task StartingDoclingWhileAProcessHoldsItsPortKeepsTheGuard()
    {
        mPorts.Listeners[DoclingPort] = Owner(pid: 100, "python");

        await mController.StartAsync(Docling(), Ct);

        Assert.Equal(0, mDocling.AllowCalls);
        Assert.Equal(1, mDocling.EnsureCalls);
    }

    [Fact]
    public async Task StartingDoclingWithAnUnreadableListenerTableKeepsTheGuard()
    {
        mPorts.ThrowOnRead = true;

        await mController.StartAsync(Docling(), Ct);

        Assert.Equal(0, mDocling.AllowCalls);
        Assert.Equal(1, mDocling.EnsureCalls);
    }

    [Fact]
    public async Task AnUnregisteredDoclingSaysToRegisterIt()
    {
        mDocling.Outcome = DoclingLaunchOutcome.NotRegistered;

        ComponentActionResult result = await mController.StartAsync(Docling(), Ct);

        Assert.Equal("Register Docling from the tray first.", result.Message);
    }

    [Fact]
    public async Task RestartingDoclingAfterAConfirmedFreePortAllowsARelaunchThenLaunches()
    {
        mPorts.Listeners[DoclingPort] = Owner(pid: 100, "python");
        mStopper.OnStop = () => mPorts.Listeners[DoclingPort] = null;

        ComponentActionResult result = await mController.RestartAsync(Docling(), Ct);

        Assert.Equal(ComponentActionStatus.Succeeded, result.Status);
        int allow = mEvents.IndexOf("docling allow");
        int ensure = mEvents.IndexOf("docling ensure");
        Assert.True(allow >= 0 && ensure > allow);
        Assert.Equal(1, mDocling.AllowCalls);
        Assert.Equal(1, mDocling.EnsureCalls);
    }

    [Fact]
    public async Task RestartingDoclingThatWasRespawnedDoesNotClearTheGuard()
    {
        mPorts.Listeners[DoclingPort] = Owner(pid: 100, "python");
        mStopper.OnStop = () => mPorts.Listeners[DoclingPort] = Owner(pid: 101, "python");

        await mController.RestartAsync(Docling(), Ct);

        Assert.Equal(0, mDocling.AllowCalls);
        Assert.Equal(1, mDocling.EnsureCalls);
    }

    [Fact]
    public async Task RestartingDoclingThatIsStillLoadingDoesNotClearTheGuard()
    {
        mDocling.LaunchedProcessIsRunning = true;

        await mController.RestartAsync(Docling(), Ct);

        Assert.Equal(0, mDocling.AllowCalls);
        Assert.Equal(1, mDocling.EnsureCalls);
    }

    [Fact]
    public async Task RestartingDoclingAfterItsLaunchedProcessExitedClearsTheGuard()
    {
        mDocling.LaunchedProcessIsRunning = false;

        await mController.RestartAsync(Docling(), Ct);

        Assert.Equal(1, mDocling.AllowCalls);
        Assert.Equal(1, mDocling.EnsureCalls);
    }

    [Fact]
    public async Task ACancelledStopMeansNoDoclingLaunch()
    {
        mPorts.Listeners[DoclingPort] = Owner(pid: 100, "python");
        mConfirmation.Answer = false;

        ComponentActionResult result = await mController.RestartAsync(Docling(), Ct);

        Assert.Equal(ComponentActionStatus.Cancelled, result.Status);
        Assert.Equal(0, mDocling.EnsureCalls);
    }

    #endregion

    #region Starting and restarting Ollama

    [Fact]
    public async Task AHealthyOllamaIsNotStartedAgain()
    {
        mPorts.Listeners[OllamaPort] = Owner(pid: 100, "ollama");
        mHandler.Healthy = true;

        ComponentActionResult result = await mController.StartAsync(Ollama(), Ct);

        Assert.Equal(ComponentActionStatus.Succeeded, result.Status);
        Assert.Equal("Ollama is already running.", result.Message);
        Assert.Empty(mStarter.Started);
    }

    [Fact]
    public async Task AListenerThatIsNotAnsweringYetIsWaitedForRatherThanJoinedByASecondServer()
    {
        mPorts.Listeners[OllamaPort] = Owner(pid: 100, "ollama");
        mHandler.Healthy = false;

        ComponentActionResult result = await mController.StartAsync(Ollama(), Ct);

        Assert.Equal(ComponentActionStatus.TimedOut, result.Status);
        Assert.Empty(mStarter.Started);
    }

    [Fact]
    public async Task OllamaIsStartedAsServeWithItsModelFolderUnelevated()
    {
        mStarter.OnStart = () =>
                           {
                               mPorts.Listeners[OllamaPort] = Owner(pid: 300, "ollama");
                               mHandler.Healthy = true;
                           };

        ComponentActionResult result = await mController.StartAsync(Ollama(), Ct);

        Assert.Equal(ComponentActionStatus.Succeeded, result.Status);
        ProcessStartInfo started = Assert.Single(mStarter.Started);
        Assert.Equal(smOllamaExe, started.FileName);
        Assert.Equal("serve", started.Arguments);
        Assert.Equal(smOllamaModels, started.Environment["OLLAMA_MODELS"]);
        Assert.False(started.UseShellExecute);
        Assert.True(started.CreateNoWindow);
        Assert.True(started.RedirectStandardOutput);
        Assert.True(started.RedirectStandardError);
        Assert.NotEqual("runas", started.Verb);
    }

    [Fact]
    public async Task WithoutAModelFolderOllamaModelsIsLeftAlone()
    {
        string? inherited = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        var row = new ComponentDefinition(ComponentKind.Ollama, "Ollama", new Uri("http://localhost:11434"), OllamaPort,
                                          HostedLocally: true, WindowsServiceName: null, smOllamaExe, ModelsDirectory: null);
        mStarter.OnStart = () =>
                           {
                               mPorts.Listeners[OllamaPort] = Owner(pid: 300, "ollama");
                               mHandler.Healthy = true;
                           };

        await mController.StartAsync(row, Ct);

        ProcessStartInfo started = Assert.Single(mStarter.Started);
        Assert.Equal(inherited, started.Environment.TryGetValue("OLLAMA_MODELS", out string? value) ? value : null);
    }

    [Fact]
    public async Task AnOllamaThatNeverAnswersTimesOutAfterSixtySeconds()
    {
        ComponentActionResult result = await mController.StartAsync(Ollama(), Ct);

        Assert.Equal(ComponentActionStatus.TimedOut, result.Status);
        Assert.Single(mStarter.Started);
        Assert.True(mTime.GetUtcNow() - mTime.Start >= TimeSpan.FromSeconds(60));
        Assert.True(mTime.GetUtcNow() - mTime.Start < TimeSpan.FromSeconds(62));
    }

    [Fact]
    public async Task OllamaWithNoExecutableSaysWhereToSetIt()
    {
        var row = new ComponentDefinition(ComponentKind.Ollama, "Ollama", new Uri("http://localhost:11434"), OllamaPort,
                                          HostedLocally: true, WindowsServiceName: null, ExecutablePath: null, smOllamaModels);

        ComponentActionResult result = await mController.StartAsync(row, Ct);

        Assert.Equal(ComponentActionStatus.Failed, result.Status);
        Assert.Equal("Set Ollama:ExecutablePath in SaddleRAG's settings.", result.Message);
        Assert.Empty(mStarter.Started);
    }

    [Fact]
    public async Task OllamaKilledAndRespawnedUnderANewPidSucceedsWithoutAStart()
    {
        mPorts.Listeners[OllamaPort] = Owner(pid: 100, "ollama");
        mHandler.Healthy = true;
        mStopper.OnStop = () => mPorts.Listeners[OllamaPort] = Owner(pid: 200, "ollama");

        ComponentActionResult result = await mController.RestartAsync(Ollama(), Ct);

        Assert.Equal(ComponentActionStatus.Succeeded, result.Status);
        Assert.Empty(mStarter.Started);
    }

    [Fact]
    public void TheOllamaLaunchLogLivesBesideTheDoclingLaunchLog()
    {
        Assert.EndsWith(Path.Combine("SaddleRAG", "logs", "ollama-launch.log"), ComponentController.OllamaLaunchLogPath,
                        StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Restart all

    [Fact]
    public async Task RestartAllGoesDoclingThenOllamaThenOneElevatedCallWithMongoFirst()
    {
        mHandler.Healthy = true;
        mPorts.Listeners[OllamaPort] = Owner(pid: 100, "ollama");
        mStopper.OnStop = () => mPorts.Listeners[OllamaPort] = Owner(pid: 200, "ollama");

        IReadOnlyList<ComponentActionResult> results = await mController.RestartAllAsync(AllLocal(), Ct);

        Assert.Equal([ComponentKind.Docling, ComponentKind.Ollama, ComponentKind.MongoDb, ComponentKind.SaddleRagService],
                     results.Select(result => result.Kind));
        Assert.All(results, result => Assert.Equal(ComponentActionStatus.Succeeded, result.Status));
        (IReadOnlyList<string> names, ServiceAction action) = Assert.Single(mServices.Calls);
        Assert.Equal(["MongoDB", "SaddleRAGMcp"], names);
        Assert.Equal(ServiceAction.Restart, action);
        int docling = mEvents.IndexOf("docling ensure");
        int ollama = mEvents.IndexOf("kill ollama 100");
        int elevated = mEvents.FindIndex(entry => entry.StartsWith("elevated", StringComparison.Ordinal));
        Assert.True(docling >= 0 && ollama > docling && elevated > ollama);
    }

    [Fact]
    public async Task RestartAllStopsAtTheFirstFailureAndNamesWhatWasNotRestarted()
    {
        mDocling.Outcome = DoclingLaunchOutcome.Timeout;

        IReadOnlyList<ComponentActionResult> results = await mController.RestartAllAsync(AllLocal(), Ct);

        ComponentActionResult only = Assert.Single(results);
        Assert.Equal(ComponentKind.Docling, only.Kind);
        Assert.Equal(ComponentActionStatus.TimedOut, only.Status);
        Assert.Contains("Ollama", only.Message, StringComparison.Ordinal);
        Assert.Contains("MongoDB", only.Message, StringComparison.Ordinal);
        Assert.Contains("SaddleRAG", only.Message, StringComparison.Ordinal);
        Assert.Empty(mServices.Calls);
        Assert.Empty(mStarter.Started);
    }

    [Fact]
    public async Task RestartAllSkipsAnUnregisteredDoclingAndStillRestartsTheRest()
    {
        mDocling.Outcome = DoclingLaunchOutcome.NotRegistered;
        mHandler.Healthy = true;
        mPorts.Listeners[OllamaPort] = Owner(pid: 100, "ollama");
        mStopper.OnStop = () => mPorts.Listeners[OllamaPort] = Owner(pid: 200, "ollama");

        IReadOnlyList<ComponentActionResult> results = await mController.RestartAllAsync(AllLocal(), Ct);

        Assert.Equal([ComponentActionStatus.NotControllable, ComponentActionStatus.Succeeded,
                      ComponentActionStatus.Succeeded, ComponentActionStatus.Succeeded],
                     results.Select(result => result.Status));
        Assert.Equal("Docling is not registered; skipped.", results[0].Message);
        Assert.Contains("kill ollama 100", mEvents);
        (IReadOnlyList<string> names, ServiceAction action) = Assert.Single(mServices.Calls);
        Assert.Equal(["MongoDB", "SaddleRAGMcp"], names);
        Assert.Equal(ServiceAction.Restart, action);
    }

    [Fact]
    public async Task RestartAllSkipsAStoppedOllamaWithNoExecutableAndStillRestartsTheRest()
    {
        var ollama = new ComponentDefinition(ComponentKind.Ollama, "Ollama", new Uri("http://localhost:11434"), OllamaPort,
                                             HostedLocally: true, WindowsServiceName: null, ExecutablePath: null,
                                             smOllamaModels);
        IReadOnlyList<ComponentDefinition> rows = [SaddleRag(), Mongo(), Docling(), ollama];

        IReadOnlyList<ComponentActionResult> results = await mController.RestartAllAsync(rows, Ct);

        Assert.Equal([ComponentActionStatus.Succeeded, ComponentActionStatus.NotControllable,
                      ComponentActionStatus.Succeeded, ComponentActionStatus.Succeeded],
                     results.Select(result => result.Status));
        Assert.Equal("Ollama is not running and no executable path is configured; skipped.", results[1].Message);
        Assert.Empty(mStarter.Started);
        Assert.Equal(["MongoDB", "SaddleRAGMcp"], Assert.Single(mServices.Calls).Names);
    }

    [Fact]
    public async Task RestartAllStillHaltsWhenAnUnregisteredDoclingWasRunningAndHasBeenStopped()
    {
        // The operator agreed to stop a Docling the tray then cannot start: that is a real failure.
        mDocling.Outcome = DoclingLaunchOutcome.NotRegistered;
        mPorts.Listeners[DoclingPort] = Owner(pid: 100, "python");
        mStopper.OnStop = () => mPorts.Listeners[DoclingPort] = null;

        IReadOnlyList<ComponentActionResult> results = await mController.RestartAllAsync(AllLocal(), Ct);

        ComponentActionResult only = Assert.Single(results);
        Assert.Equal(ComponentActionStatus.Failed, only.Status);
        Assert.Empty(mServices.Calls);
    }

    [Fact]
    public async Task OutsideRestartAllTheUnconfiguredRowsStillFail()
    {
        mDocling.Outcome = DoclingLaunchOutcome.NotRegistered;
        var ollama = new ComponentDefinition(ComponentKind.Ollama, "Ollama", new Uri("http://localhost:11434"), OllamaPort,
                                             HostedLocally: true, WindowsServiceName: null, ExecutablePath: null,
                                             smOllamaModels);

        ComponentActionResult docling = await mController.RestartAsync(Docling(), Ct);
        ComponentActionResult ollamaResult = await mController.RestartAsync(ollama, Ct);

        Assert.Equal(ComponentActionStatus.Failed, docling.Status);
        Assert.Equal("Register Docling from the tray first.", docling.Message);
        Assert.Equal(ComponentActionStatus.Failed, ollamaResult.Status);
        Assert.Equal("Set Ollama:ExecutablePath in SaddleRAG's settings.", ollamaResult.Message);
    }

    [Fact]
    public async Task RestartAllCarriesOnPastRemoteRows()
    {
        IReadOnlyList<ComponentDefinition> rows = [SaddleRag(), Mongo(local: false), Docling(local: false), Ollama(local: false)];

        IReadOnlyList<ComponentActionResult> results = await mController.RestartAllAsync(rows, Ct);

        Assert.Equal([ComponentActionStatus.NotControllable, ComponentActionStatus.NotControllable,
                      ComponentActionStatus.NotControllable, ComponentActionStatus.Succeeded],
                     results.Select(result => result.Status));
        Assert.Equal(["SaddleRAGMcp"], Assert.Single(mServices.Calls).Names);
        Assert.Equal(0, mDocling.EnsureCalls);
    }

    [Fact]
    public async Task RestartAllWithALocalMongoThatIsNotAServiceRestartsOnlySaddleRag()
    {
        mHandler.Healthy = true;
        mPorts.Listeners[OllamaPort] = Owner(pid: 100, "ollama");
        mStopper.OnStop = () => mPorts.Listeners[OllamaPort] = Owner(pid: 200, "ollama");
        IReadOnlyList<ComponentDefinition> rows = [SaddleRag(), Mongo(serviceName: null), Docling(), Ollama()];

        IReadOnlyList<ComponentActionResult> results = await mController.RestartAllAsync(rows, Ct);

        ComponentActionResult mongo = Assert.Single(results, result => result.Kind == ComponentKind.MongoDb);
        Assert.Equal(ComponentActionStatus.NotControllable, mongo.Status);
        Assert.Equal(["SaddleRAGMcp"], Assert.Single(mServices.Calls).Names);
    }

    [Fact]
    public async Task ADeclinedElevationEndsRestartAllAsCancelledForBothServices()
    {
        mHandler.Healthy = true;
        mPorts.Listeners[OllamaPort] = Owner(pid: 100, "ollama");
        mStopper.OnStop = () => mPorts.Listeners[OllamaPort] = Owner(pid: 200, "ollama");
        mServices.Outcome = ServiceActionOutcome.Cancelled;

        IReadOnlyList<ComponentActionResult> results = await mController.RestartAllAsync(AllLocal(), Ct);

        Assert.Equal(ComponentActionStatus.Cancelled, results.Single(result => result.Kind == ComponentKind.MongoDb).Status);
        Assert.Equal(ComponentActionStatus.Cancelled,
                     results.Single(result => result.Kind == ComponentKind.SaddleRagService).Status);
    }

    #endregion

    #region Logging

    [Fact]
    public async Task OutcomesLogAtTheContractLevelNamingComponentAndAction()
    {
        mServices.Outcome = ServiceActionOutcome.Succeeded;
        await mController.StartAsync(SaddleRag(), Ct);

        mPorts.Listeners[DoclingPort] = Owner(pid: 100, "python");
        mConfirmation.Answer = false;
        await mController.StopAsync(Docling(), Ct);

        mConfirmation.Answer = true;
        mPorts.Listeners[OllamaPort] = Owner(pid: 200, "ollama");
        mStopper.Outcome = ProcessStopOutcome.AccessDenied;
        await mController.StopAsync(Ollama(), Ct);

        mServices.Outcome = ServiceActionOutcome.TimedOut;
        await mController.RestartAsync(Mongo(), Ct);

        mServices.Outcome = ServiceActionOutcome.Failed;
        await mController.StopAsync(SaddleRag(), Ct);

        Assert.Equal([LogLevel.Information, LogLevel.Information, LogLevel.Warning, LogLevel.Warning, LogLevel.Warning],
                     mLogger.Entries.Select(entry => entry.Level));
        AssertNames(mLogger.Entries[0].Message, "SaddleRAG", "start");
        AssertNames(mLogger.Entries[1].Message, "Docling", "stop");
        AssertNames(mLogger.Entries[2].Message, "Ollama", "stop");
        AssertNames(mLogger.Entries[3].Message, "MongoDB", "restart");
        AssertNames(mLogger.Entries[4].Message, "SaddleRAG", "stop");
    }

    [Fact]
    public async Task AnExceptionThatEscapesIsLoggedAsErrorAndRethrown()
    {
        var runner = Substitute.For<IServiceActionRunner>();
        runner.RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<ServiceAction>(), Arg.Any<CancellationToken>())
              .Returns<Task<ServiceActionOutcome>>(_ => throw new InvalidOperationException("boom"));
        var controller = new ComponentController(runner, mProbe, mPorts, mStopper, mDocling, mStarter, mConfirmation,
                                                 mLogger, mTime);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(SaddleRag(), Ct));

        (LogLevel level, string message) = Assert.Single(mLogger.Entries);
        Assert.Equal(LogLevel.Error, level);
        AssertNames(message, "SaddleRAG", "start");

        // The guard was released on the way out, so the next action is not refused as busy.
        Assert.Equal(ComponentActionStatus.AlreadyInState, (await controller.StopAsync(Docling(), Ct)).Status);
    }

    private static void AssertNames(string message, string component, string action)
    {
        Assert.Contains(component, message, StringComparison.Ordinal);
        Assert.Contains(action, message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
