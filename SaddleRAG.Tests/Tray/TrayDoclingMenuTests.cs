// TrayDoclingMenuTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging;
using SaddleRAG.Tests.Documents.Docling;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

public sealed class TrayDoclingMenuTests
{
    private sealed class FakeLaunchRequestProbe : IDoclingLaunchRequestProbe
    {
        public bool Requested { get; set; }
        public bool Throw { get; set; }
        public int Calls { get; private set; }

        public Task<bool> IsLaunchRequestedAsync(CancellationToken ct = default)
        {
            Calls++;
            return Throw
                       ? Task.FromException<bool>(new HttpRequestException("monitor is not listening"))
                       : Task.FromResult(Requested);
        }
    }

    private sealed class FakeLauncher : IDoclingLauncher
    {
        public int Calls { get; private set; }

        public Task<DoclingLaunchOutcome> EnsureRunningAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(DoclingLaunchOutcome.Ready);
        }

        public bool IsRegistered => true;

        public bool TryAllowRelaunch(Func<bool> portIsFree) => false;
    }

    private static string TrayFile(string fileName) =>
        File.ReadAllText(Path.Combine(DoclingTestSupport.RepositoryRoot(), "SaddleRAG.Tray", fileName));

    [Fact]
    public async Task APositiveLaunchRequestTriggersExactlyOneLaunch()
    {
        FakeLaunchRequestProbe probe = new() { Requested = true };
        FakeLauncher launcher = new();
        DoclingLaunchCoordinator coordinator = new(probe, launcher);

        DoclingLaunchOutcome? outcome = await coordinator.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoclingLaunchOutcome.Ready, outcome);
        Assert.Equal(expected: 1, launcher.Calls);
    }

    [Fact]
    public async Task ANegativeLaunchRequestStartsNothing()
    {
        FakeLaunchRequestProbe probe = new() { Requested = false };
        FakeLauncher launcher = new();
        DoclingLaunchCoordinator coordinator = new(probe, launcher);

        DoclingLaunchOutcome? outcome = await coordinator.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Null(outcome);
        Assert.Equal(expected: 0, launcher.Calls);
    }

    [Fact]
    public async Task AFailingPollIsSwallowedSoTheTimerSurvives()
    {
        FakeLaunchRequestProbe probe = new() { Throw = true };
        FakeLauncher launcher = new();
        DoclingLaunchCoordinator coordinator = new(probe, launcher);

        DoclingLaunchOutcome? outcome = await coordinator.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Null(outcome);
        Assert.Equal(expected: 0, launcher.Calls);
    }

    private sealed class RecordingCoordinatorLogger : ILogger<DoclingLaunchCoordinator>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel,
                                EventId eventId,
                                TState state,
                                Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
        }
    }

    [Fact]
    public async Task AStopByTheOperatorKeepsAWaitingRequestFromLaunchingUntilTheyStartAgain()
    {
        FakeLaunchRequestProbe probe = new() { Requested = true };
        FakeLauncher launcher = new();
        DoclingOperatorIntent intent = new();
        DoclingLaunchCoordinator coordinator = new(probe, launcher, logger: null, intent);

        intent.SuppressAutoStart();
        DoclingLaunchOutcome? suppressed = await coordinator.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Null(suppressed);
        Assert.Equal(expected: 0, launcher.Calls);

        intent.LiftAutoStartSuppression();
        DoclingLaunchOutcome? lifted = await coordinator.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoclingLaunchOutcome.Ready, lifted);
        Assert.Equal(expected: 1, launcher.Calls);
    }

    [Fact]
    public async Task ASuppressedPollLogsInformationOncePerSuppressionNotEveryTick()
    {
        FakeLaunchRequestProbe probe = new() { Requested = true };
        FakeLauncher launcher = new();
        DoclingOperatorIntent intent = new();
        RecordingCoordinatorLogger logger = new();
        DoclingLaunchCoordinator coordinator = new(probe, launcher, logger, intent);

        intent.SuppressAutoStart();
        for (var tick = 0; tick < 3; tick++)
            await coordinator.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal([LogLevel.Information], logger.Levels);

        intent.LiftAutoStartSuppression();
        intent.SuppressAutoStart();
        await coordinator.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal([LogLevel.Information, LogLevel.Information], logger.Levels);
    }

    [Fact]
    public async Task ASuppressedPollWithNoRequestWaitingLogsNothing()
    {
        FakeLaunchRequestProbe probe = new() { Requested = false };
        DoclingOperatorIntent intent = new();
        RecordingCoordinatorLogger logger = new();
        DoclingLaunchCoordinator coordinator = new(probe, new FakeLauncher(), logger, intent);

        intent.SuppressAutoStart();
        await coordinator.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(logger.Levels);
    }

    [Fact]
    public void RelayCommandInvokesItsAction()
    {
        var invoked = false;
        RelayCommand command = new(() => invoked = true);

        command.Execute(parameter: null);

        Assert.True(invoked);
        Assert.True(command.CanExecute(parameter: null));
    }

    [Fact]
    public void TheTrayMenuOffersDocumentScanningActions()
    {
        string xaml = TrayFile("App.xaml");

        Assert.Contains("Document scanning", xaml, StringComparison.Ordinal);
        Assert.Contains("Register Docling", xaml, StringComparison.Ordinal);
        Assert.Contains("Register Tesseract", xaml, StringComparison.Ordinal);
        Assert.Contains("Start Docling", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleClickOpensTheDashboardThroughTheSameCodePathAsTheMenuItem()
    {
        string code = TrayFile("App.xaml.cs");

        // Both gestures must funnel through one method, so a fix to either reaches both.
        Assert.Contains("DoubleClickCommand = new RelayCommand(OpenDashboardGuarded)", code, StringComparison.Ordinal);
        Assert.Contains("private void OpenDashboardGuarded()", code, StringComparison.Ordinal);
        Assert.Contains("OnOpenDashboard(object sender, RoutedEventArgs e) => OpenDashboardGuarded()",
                        code,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrayOnlyPollsWhileADirectoryLibraryIsRegistered()
    {
        string code = TrayFile("App.xaml.cs");

        Assert.Contains("DoclingLaunchCoordinator", code, StringComparison.Ordinal);
        Assert.Contains("DoclingPollIntervalSeconds", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrayMenuOffersTheComponentsWindowDirectlyUnderServiceHealth()
    {
        string xaml = TrayFile("App.xaml");

        Assert.Contains("<MenuItem Header=\"Components…\" Click=\"OnOpenComponents\"/>", xaml, StringComparison.Ordinal);
        int health = xaml.IndexOf("Header=\"Service health\"", StringComparison.Ordinal);
        int components = xaml.IndexOf("Header=\"Components…\"", StringComparison.Ordinal);
        int warnings = xaml.IndexOf("Header=\"Server warnings and errors\"", StringComparison.Ordinal);
        Assert.True(health >= 0 && health < components && components < warnings);
    }

    [Fact]
    public void TheComponentsWindowIsOpenedOnceAndReusesTheSingleServiceRunner()
    {
        string code = TrayFile("App.xaml.cs");

        Assert.Contains("private async void OnOpenComponents(object sender, RoutedEventArgs e)", code, StringComparison.Ordinal);
        Assert.Contains("new TcpTablePortOwnerLookup()", code, StringComparison.Ordinal);
        Assert.Contains("new MessageBoxOperatorConfirmation()", code, StringComparison.Ordinal);
        Assert.Contains("new ProcessStarter(ComponentController.OllamaLaunchLogPath)", code, StringComparison.Ordinal);
        Assert.Equal(expected: 1, CountOccurrences(code, "new ElevatedServiceActionRunner("));
        Assert.Equal(expected: 1, CountOccurrences(code, "new DoclingProcessLauncher("));
        Assert.Equal(expected: 1, CountOccurrences(code, "DoclingOperatorIntent mDoclingIntent = new();"));
        Assert.Contains("mDoclingIntent);", code, StringComparison.Ordinal);
        Assert.Contains("doclingIntent: mDoclingIntent", code, StringComparison.Ordinal);
        Assert.Contains("mDoclingIntent.LiftAutoStartSuppression();", code, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
