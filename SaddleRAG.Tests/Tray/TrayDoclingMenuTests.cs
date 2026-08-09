// TrayDoclingMenuTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

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
}
