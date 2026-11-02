// DoclingProcessLauncherTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Diagnostics;
using System.Net;
using System.Text;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

public sealed class DoclingProcessLauncherTests : IDisposable
{
    public DoclingProcessLauncherTests()
    {
        DoclingProcessLauncher.ResetLaunchGuardForTesting();
        mTempDir = Path.Combine(Path.GetTempPath(), $"saddlerag-launcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mTempDir);
        mRegistryPath = Path.Combine(mTempDir, "external-tools.json");
        mCommandPath = Path.Combine(mTempDir, "start-docling.cmd");
        File.WriteAllText(mCommandPath, "rem stand-in for the user's registered command");
    }

    private readonly string mTempDir;
    private readonly string mRegistryPath;
    private readonly string mCommandPath;

    public void Dispose()
    {
        DoclingProcessLauncher.ResetLaunchGuardForTesting();
        if (Directory.Exists(mTempDir))
            Directory.Delete(mTempDir, recursive: true);
    }

    private sealed class StubHealthHandler : HttpMessageHandler
    {
        public StubHealthHandler(Func<int, HttpResponseMessage> respond)
        {
            mRespond = respond;
        }

        private readonly Func<int, HttpResponseMessage> mRespond;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(mRespond(Calls));
        }
    }

    private sealed class FakeProcessStarter : IProcessStarter
    {
        public List<ProcessStartInfo> Started { get; } = [];
        public List<FakeLaunchedProcess> Handles { get; } = [];
        public bool ThrowOnStart { get; set; }

        /// <summary>Whether every process this starter launched has exited.</summary>
        public bool LaunchedHaveExited { get; set; }

        public IDisposable? Start(ProcessStartInfo startInfo)
        {
            Started.Add(startInfo);
            if (ThrowOnStart)
                throw new InvalidOperationException("the registered command could not be started");

            var handle = new FakeLaunchedProcess(this);
            Handles.Add(handle);
            return handle;
        }
    }

    private sealed class FakeLaunchedProcess : ILaunchedProcess
    {
        public FakeLaunchedProcess(FakeProcessStarter starter)
        {
            mStarter = starter;
        }

        private readonly FakeProcessStarter mStarter;

        public bool Disposed { get; private set; }

        public bool HasExited => mStarter.LaunchedHaveExited;

        public void Dispose() => Disposed = true;
    }

    private static readonly ComponentDefinition smDoclingRow =
        new(ComponentKind.Docling, "Docling", new Uri("http://localhost:5001"), Port: 5001, HostedLocally: true,
            WindowsServiceName: null, ExecutablePath: null, ModelsDirectory: null);

    /// <summary>A controller around the real launcher; every other seam is an inert stand-in and the port is free.</summary>
    private static ComponentController ControllerFor(DoclingProcessLauncher launcher, HttpClient client)
    {
        IPortOwnerLookup ports = Substitute.For<IPortOwnerLookup>();
        ports.FindListeningProcess(Arg.Any<int>()).Returns((ListeningProcess?) null);
        var probe = new ComponentStatusProbe(client, Substitute.For<IServiceStateReader>(), time: null, ports);
        return new ComponentController(Substitute.For<IServiceActionRunner>(),
                                       probe,
                                       ports,
                                       Substitute.For<IUserProcessStopper>(),
                                       launcher,
                                       Substitute.For<IProcessStarter>(),
                                       Substitute.For<IOperatorConfirmation>());
    }

    private static HttpResponseMessage Healthy() =>
        new(HttpStatusCode.OK) { Content = new StringContent("""{"status":"ok"}""", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Unhealthy() => new(HttpStatusCode.ServiceUnavailable);

    private void WriteRegistry(DoclingRegistration? docling, TesseractRegistration? tesseract = null) =>
        new ExternalToolRegistry(mRegistryPath).Write(new ExternalToolRegistration(docling, tesseract));

    private DoclingRegistration RegisteredCommand(IReadOnlyDictionary<string, string>? environment = null) =>
        new(mCommandPath, "--serve", mTempDir, environment);

    private (DoclingProcessLauncher Launcher, FakeProcessStarter Starter, StubHealthHandler Handler, HttpClient Client)
        MakeLauncher(Func<int, HttpResponseMessage> respond)
    {
        StubHealthHandler handler = new(respond);
        HttpClient client = new(handler);
        FakeProcessStarter starter = new();
        MutableTimeProvider time = new(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
        DoclingLaunchSettings settings = new(new Uri("http://localhost:5001"),
                                             ReadinessTimeout: TimeSpan.FromSeconds(seconds: 120),
                                             PollInterval: TimeSpan.FromMilliseconds(value: 1));
        DoclingProcessLauncher launcher = new(client,
                                              new ExternalToolRegistry(mRegistryPath),
                                              starter,
                                              new FileSystemProbe(),
                                              settings,
                                              logger: null,
                                              time);
        return (launcher, starter, handler, client);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            mUtcNow = utcNow;
        }

        private DateTimeOffset mUtcNow;

        public override DateTimeOffset GetUtcNow()
        {
            // Every read advances the clock, so a bounded readiness wait terminates without
            // the test spending real wall-clock time waiting for it.
            mUtcNow += TimeSpan.FromSeconds(value: 10);
            return mUtcNow;
        }
    }

    [Fact]
    public async Task AHealthyEndpointShortCircuitsWithoutStartingAnything()
    {
        WriteRegistry(RegisteredCommand());
        (DoclingProcessLauncher launcher, FakeProcessStarter starter, _, HttpClient client) = MakeLauncher(_ => Healthy());
        using(client)
        {
            DoclingLaunchOutcome outcome = await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);

            Assert.Equal(DoclingLaunchOutcome.AlreadyRunning, outcome);
            Assert.Empty(starter.Started);
        }
    }

    [Fact]
    public async Task AnUnregisteredDoclingIsReportedRatherThanGuessed()
    {
        WriteRegistry(docling: null);
        (DoclingProcessLauncher launcher, FakeProcessStarter starter, _, HttpClient client) =
            MakeLauncher(_ => Unhealthy());
        using(client)
        {
            DoclingLaunchOutcome outcome = await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);

            Assert.Equal(DoclingLaunchOutcome.NotRegistered, outcome);
            Assert.Empty(starter.Started);
        }
    }

    [Fact]
    public async Task TheRegisteredCommandIsStartedAndReportedReadyWhenHealthAnswers()
    {
        WriteRegistry(RegisteredCommand());
        (DoclingProcessLauncher launcher, FakeProcessStarter starter, _, HttpClient client) =
            MakeLauncher(call => call == 1 ? Unhealthy() : Healthy());
        using(client)
        {
            DoclingLaunchOutcome outcome = await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);

            Assert.Equal(DoclingLaunchOutcome.Ready, outcome);
            ProcessStartInfo started = Assert.Single(starter.Started);
            Assert.Equal(mCommandPath, started.FileName);
            Assert.Equal("--serve", started.Arguments);
            Assert.Equal(mTempDir, started.WorkingDirectory);
            Assert.False(started.UseShellExecute);
            Assert.True(started.CreateNoWindow);
            Assert.True(started.RedirectStandardOutput);
            Assert.True(started.RedirectStandardError);
        }
    }

    [Fact]
    public async Task TheSingleShotGuardBlocksASecondSpawnInOneSession()
    {
        WriteRegistry(RegisteredCommand());
        (DoclingProcessLauncher launcher, FakeProcessStarter starter, _, HttpClient client) =
            MakeLauncher(_ => Unhealthy());
        using(client)
        {
            await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);
            await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);

            Assert.Single(starter.Started);
        }
    }

    // Lives here rather than in ComponentControllerTests because the guard is process-wide: this
    // class runs its tests one at a time and resets the guard around each, so no other test races it.
    [Fact]
    public async Task ClearingTheGuardOnceTheLaunchedProcessExitedLetsTheNextLaunchStartDoclingAgain()
    {
        WriteRegistry(RegisteredCommand());
        (DoclingProcessLauncher launcher, FakeProcessStarter starter, _, HttpClient client) =
            MakeLauncher(_ => Unhealthy());
        using(client)
        {
            DoclingProcessLauncher.ResetLaunchGuardForTesting();
            await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);
            starter.LaunchedHaveExited = true;

            Assert.True(launcher.TryAllowRelaunch(() => true));
            await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);

            Assert.Equal(expected: 2, starter.Started.Count);
        }
    }

    [Fact]
    public async Task TheGuardIsNotClearedWhileTheLaunchedProcessRunsEvenWithThePortFree()
    {
        WriteRegistry(RegisteredCommand());
        (DoclingProcessLauncher launcher, FakeProcessStarter starter, _, HttpClient client) =
            MakeLauncher(_ => Unhealthy());
        using(client)
        {
            await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);
            starter.LaunchedHaveExited = false;
            var portAsked = false;

            bool cleared = launcher.TryAllowRelaunch(() =>
                                                     {
                                                         portAsked = true;
                                                         return true;
                                                     });
            await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);

            Assert.False(cleared);
            Assert.False(portAsked);
            Assert.Single(starter.Started);
        }
    }

    [Fact]
    public async Task TheGuardIsNotClearedWhileThePortIsHeldEvenWithTheLaunchedProcessGone()
    {
        WriteRegistry(RegisteredCommand());
        (DoclingProcessLauncher launcher, FakeProcessStarter starter, _, HttpClient client) =
            MakeLauncher(_ => Unhealthy());
        using(client)
        {
            await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);
            starter.LaunchedHaveExited = true;

            bool cleared = launcher.TryAllowRelaunch(() => false);
            await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);

            Assert.False(cleared);
            Assert.Single(starter.Started);
        }
    }

    [Fact]
    public void IsRegisteredFollowsTheRegistry()
    {
        (DoclingProcessLauncher launcher, _, _, HttpClient client) = MakeLauncher(_ => Unhealthy());
        using(client)
        {
            WriteRegistry(docling: null);
            Assert.False(launcher.IsRegistered);

            WriteRegistry(RegisteredCommand());
            Assert.True(launcher.IsRegistered);
        }
    }

    [Fact]
    public async Task StartingADoclingThatCrashedAfterLaunchLaunchesItAgain()
    {
        WriteRegistry(RegisteredCommand());
        (DoclingProcessLauncher launcher, FakeProcessStarter starter, _, HttpClient client) =
            MakeLauncher(_ => Unhealthy());
        using(client)
        {
            await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);
            starter.LaunchedHaveExited = true;

            await ControllerFor(launcher, client).StartAsync(smDoclingRow, TestContext.Current.CancellationToken);

            Assert.Equal(expected: 2, starter.Started.Count);
            Assert.True(starter.Handles[0].Disposed);
            Assert.False(starter.Handles[1].Disposed);
        }
    }

    [Fact]
    public async Task StartingADoclingStillLoadingDoesNotLaunchASecondOne()
    {
        WriteRegistry(RegisteredCommand());
        (DoclingProcessLauncher launcher, FakeProcessStarter starter, _, HttpClient client) =
            MakeLauncher(_ => Unhealthy());
        using(client)
        {
            await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);
            starter.LaunchedHaveExited = false;

            await ControllerFor(launcher, client).StartAsync(smDoclingRow, TestContext.Current.CancellationToken);

            Assert.Single(starter.Started);
            Assert.False(starter.Handles[0].Disposed);
        }
    }

    [Fact]
    public async Task TesseractRegistrationInjectsTessdataPrefixWithATrailingSeparator()
    {
        string tessDirectory = Path.Combine(mTempDir, "Tesseract-OCR");
        string tessdata = Path.Combine(tessDirectory, "tessdata");
        WriteRegistry(RegisteredCommand(), new TesseractRegistration(tessDirectory, tessdata));
        (DoclingProcessLauncher launcher, FakeProcessStarter starter, _, HttpClient client) =
            MakeLauncher(call => call == 1 ? Unhealthy() : Healthy());
        using(client)
        {
            await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);

            ProcessStartInfo started = Assert.Single(starter.Started);
            string prefix = started.Environment["TESSDATA_PREFIX"] ?? string.Empty;
            Assert.Equal(tessdata + Path.DirectorySeparatorChar, prefix);
            Assert.StartsWith(tessDirectory + Path.PathSeparator,
                              started.Environment["PATH"] ?? string.Empty,
                              StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WithoutATesseractRegistrationNoTessdataPrefixIsInjected()
    {
        WriteRegistry(RegisteredCommand());
        (DoclingProcessLauncher launcher, FakeProcessStarter starter, _, HttpClient client) =
            MakeLauncher(call => call == 1 ? Unhealthy() : Healthy());
        using(client)
        {
            await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);

            ProcessStartInfo started = Assert.Single(starter.Started);
            Assert.False(started.Environment.ContainsKey("TESSDATA_PREFIX"));
        }
    }

    [Fact]
    public async Task RegisteredEnvironmentEntriesReachTheChildProcess()
    {
        WriteRegistry(RegisteredCommand(new Dictionary<string, string> { ["PYTHONUTF8"] = "1" }));
        (DoclingProcessLauncher launcher, FakeProcessStarter starter, _, HttpClient client) =
            MakeLauncher(call => call == 1 ? Unhealthy() : Healthy());
        using(client)
        {
            await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);

            ProcessStartInfo started = Assert.Single(starter.Started);
            Assert.Equal("1", started.Environment["PYTHONUTF8"]);
        }
    }

    [Fact]
    public async Task AServerThatNeverAnswersHealthReportsTimeout()
    {
        WriteRegistry(RegisteredCommand());
        (DoclingProcessLauncher launcher, FakeProcessStarter starter, _, HttpClient client) =
            MakeLauncher(_ => Unhealthy());
        using(client)
        {
            DoclingLaunchOutcome outcome = await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);

            Assert.Equal(DoclingLaunchOutcome.Timeout, outcome);
            Assert.Single(starter.Started);
        }
    }

    [Fact]
    public async Task ARootedCommandMissingFromDiskFailsWithoutFallingBackToPath()
    {
        WriteRegistry(new DoclingRegistration(Path.Combine(mTempDir, "gone.cmd"), "--serve", mTempDir, Environment: null));
        (DoclingProcessLauncher launcher, FakeProcessStarter starter, _, HttpClient client) =
            MakeLauncher(_ => Unhealthy());
        using(client)
        {
            DoclingLaunchOutcome outcome = await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);

            Assert.Equal(DoclingLaunchOutcome.Failed, outcome);
            Assert.Empty(starter.Started);
        }
    }

    [Fact]
    public async Task AStartThatThrowsIsReportedAsFailed()
    {
        WriteRegistry(RegisteredCommand());
        (DoclingProcessLauncher launcher, FakeProcessStarter starter, _, HttpClient client) =
            MakeLauncher(_ => Unhealthy());
        starter.ThrowOnStart = true;
        using(client)
        {
            DoclingLaunchOutcome outcome = await launcher.EnsureRunningAsync(TestContext.Current.CancellationToken);

            Assert.Equal(DoclingLaunchOutcome.Failed, outcome);
        }
    }
}
