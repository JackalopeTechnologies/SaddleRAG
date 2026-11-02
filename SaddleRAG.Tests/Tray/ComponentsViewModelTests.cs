// ComponentsViewModelTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

/// <summary>
///     The Components window's view model, driven through its commands. Every seam is a fake: nothing
///     here starts, stops or elevates a real process.
/// </summary>
public sealed class ComponentsViewModelTests : IDisposable
{
    public ComponentsViewModelTests()
    {
        mHttp = new HttpClient();
        mServices = Substitute.For<IServiceActionRunner>();
        mServices.RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<ServiceAction>(), Arg.Any<CancellationToken>())
                 .Returns(_ => Task.FromResult(ServiceActionOutcome.Succeeded));
        mController = new ComponentController(mServices,
                                              new ComponentStatusProbe(mHttp, Substitute.For<IServiceStateReader>()),
                                              Substitute.For<IPortOwnerLookup>(),
                                              Substitute.For<IUserProcessStopper>(),
                                              Substitute.For<IDoclingLauncher>(),
                                              Substitute.For<IProcessStarter>(),
                                              Substitute.For<IOperatorConfirmation>());
    }

    private readonly HttpClient mHttp;
    private readonly IServiceActionRunner mServices;
    private readonly ComponentController mController;
    private readonly Dictionary<ComponentKind, ComponentState> mStates = [];
    private readonly HashSet<ComponentKind> mAsyncThrowers = [];
    private readonly HashSet<ComponentKind> mSyncThrowers = [];
    private int mProbeCalls;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        mHttp.Dispose();
    }

    #region Rows

    private static ComponentDefinition SaddleRag() =>
        new(ComponentKind.SaddleRagService, "SaddleRAG", new Uri("http://localhost:6100"), Port: 6100,
            HostedLocally: true, "SaddleRAGMcp", ExecutablePath: null, ModelsDirectory: null);

    private static ComponentDefinition Mongo(bool local = true) =>
        new(ComponentKind.MongoDb, "MongoDB", local ? new Uri("mongodb://localhost:27017") : null, local ? 27017 : null,
            local, local ? "MongoDB" : null, ExecutablePath: null, ModelsDirectory: null);

    private static ComponentDefinition Docling() =>
        new(ComponentKind.Docling, "Docling", new Uri("http://localhost:5001"), Port: 5001,
            HostedLocally: true, WindowsServiceName: null, ExecutablePath: null, ModelsDirectory: null);

    private static ComponentDefinition RemoteOllama() =>
        new(ComponentKind.Ollama, "Ollama", new Uri("http://gpu.example.com:11434"), Port: 11434,
            HostedLocally: false, WindowsServiceName: null, ExecutablePath: null, ModelsDirectory: null);

    private ComponentsViewModel NewViewModel(params ComponentDefinition[] components) =>
        new(components, ProbeAsync, mController);

    private Task<ComponentStatus> ProbeAsync(ComponentDefinition component, CancellationToken ct)
    {
        mProbeCalls++;
        if (mSyncThrowers.Contains(component.Kind))
            throw new InvalidOperationException($"sync boom {component.DisplayName}");

        Task<ComponentStatus> result = mAsyncThrowers.Contains(component.Kind)
                                           ? Task.FromException<ComponentStatus>(
                                               new InvalidOperationException($"async boom {component.DisplayName}"))
                                           : Task.FromResult(new ComponentStatus(component.Kind,
                                                                                 mStates.GetValueOrDefault(component.Kind,
                                                                                                           ComponentState.Running),
                                                                                 $"detail {component.DisplayName}"));
        return result;
    }

    private static IReadOnlyList<CommandState> AllCommands(ComponentsViewModel viewModel)
    {
        List<CommandState> commands = [];
        foreach (ComponentRowViewModel row in viewModel.Rows)
        {
            commands.Add(new CommandState($"{row.DisplayName} start", row.StartCommand.CanExecute(parameter: null)));
            commands.Add(new CommandState($"{row.DisplayName} stop", row.StopCommand.CanExecute(parameter: null)));
            commands.Add(new CommandState($"{row.DisplayName} restart", row.RestartCommand.CanExecute(parameter: null)));
        }

        commands.Add(new CommandState("restart all", viewModel.RestartAllCommand.CanExecute(parameter: null)));
        return commands;
    }

    private sealed record CommandState(string Name, bool CanExecute);

    #endregion

    #region Rows and locations

    [Fact]
    public void RowsAppearInCatalogOrder()
    {
        ComponentsViewModel viewModel = NewViewModel(SaddleRag(), Mongo(), Docling(), RemoteOllama());

        Assert.Equal(["SaddleRAG", "MongoDB", "Docling", "Ollama"], viewModel.Rows.Select(row => row.DisplayName));
    }

    [Fact]
    public void LocationTextSaysWhereEachComponentRuns()
    {
        ComponentsViewModel viewModel = NewViewModel(SaddleRag(), RemoteOllama());

        Assert.Equal("This computer", viewModel.Rows[0].LocationText);
        Assert.Equal("Another computer", viewModel.Rows[1].LocationText);
    }

    [Fact]
    public void ARemoteRowsCommandsCannotExecuteButALocalRowsCan()
    {
        ComponentsViewModel viewModel = NewViewModel(SaddleRag(), RemoteOllama());
        ComponentRowViewModel local = viewModel.Rows[0];
        ComponentRowViewModel remote = viewModel.Rows[1];

        Assert.True(local.StartCommand.CanExecute(parameter: null));
        Assert.True(local.StopCommand.CanExecute(parameter: null));
        Assert.True(local.RestartCommand.CanExecute(parameter: null));
        Assert.False(remote.StartCommand.CanExecute(parameter: null));
        Assert.False(remote.StopCommand.CanExecute(parameter: null));
        Assert.False(remote.RestartCommand.CanExecute(parameter: null));
    }

    #endregion

    #region Refresh

    [Fact]
    public async Task RefreshShowsEachRowsStateAndDetail()
    {
        mStates[ComponentKind.MongoDb] = ComponentState.Stopped;
        mStates[ComponentKind.Docling] = ComponentState.Unreachable;
        mStates[ComponentKind.Ollama] = ComponentState.NotInstalled;
        ComponentsViewModel viewModel = NewViewModel(SaddleRag(), Mongo(), Docling(), RemoteOllama());

        await viewModel.RefreshAsync(Ct);

        Assert.Equal(["Running", "Stopped", "Not responding", "Not installed"], viewModel.Rows.Select(row => row.StateText));
        Assert.Equal(ComponentState.Stopped, viewModel.Rows[1].State);
        Assert.Equal("detail MongoDB", viewModel.Rows[1].Detail);
        Assert.NotEmpty(viewModel.LastCheckedText);
    }

    [Fact]
    public async Task AProbeThatFailsLeavesThatRowUnknownAndTheOthersRefresh()
    {
        mAsyncThrowers.Add(ComponentKind.MongoDb);
        mSyncThrowers.Add(ComponentKind.Docling);
        ComponentsViewModel viewModel = NewViewModel(SaddleRag(), Mongo(), Docling(), RemoteOllama());

        await viewModel.RefreshAsync(Ct);

        Assert.Equal("Running", viewModel.Rows[0].StateText);
        Assert.Equal("Unknown", viewModel.Rows[1].StateText);
        Assert.Contains("async boom MongoDB", viewModel.Rows[1].Detail, StringComparison.Ordinal);
        Assert.Equal("Unknown", viewModel.Rows[2].StateText);
        Assert.Contains("sync boom Docling", viewModel.Rows[2].Detail, StringComparison.Ordinal);
        Assert.Equal("Running", viewModel.Rows[3].StateText);
    }

    [Fact]
    public async Task ACancelledRefreshThrowsAndDoesNotBlockTheNextOne()
    {
        ComponentsViewModel viewModel = NewViewModel(SaddleRag());
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => viewModel.RefreshAsync(cancelled.Token));
        await viewModel.RefreshAsync(Ct);

        Assert.Equal("Running", viewModel.Rows[0].StateText);
    }

    #endregion

    #region Actions

    [Fact]
    public async Task WhileAnActionRunsEveryCommandIsDisabledThenTheRowsRefreshAndTheResultIsShown()
    {
        TaskCompletionSource<ServiceActionOutcome> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        mServices.RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<ServiceAction>(), Arg.Any<CancellationToken>())
                 .Returns(gate.Task);
        mStates[ComponentKind.SaddleRagService] = ComponentState.Stopped;
        ComponentsViewModel viewModel = NewViewModel(SaddleRag(), Mongo());
        await viewModel.RefreshAsync(Ct);
        int probesBefore = mProbeCalls;
        Assert.Equal("Stopped", viewModel.Rows[0].StateText);

        viewModel.Rows[0].StartCommand.Execute(parameter: null);

        Assert.True(viewModel.IsBusy);
        Assert.Equal("Starting SaddleRAG…", viewModel.LastActionMessage);
        Assert.All(AllCommands(viewModel), command => Assert.False(command.CanExecute, command.Name));

        mStates[ComponentKind.SaddleRagService] = ComponentState.Running;
        gate.SetResult(ServiceActionOutcome.Succeeded);
        await viewModel.ActionCompletion;

        Assert.False(viewModel.IsBusy);
        Assert.Equal("SaddleRAG started.", viewModel.LastActionMessage);
        Assert.Equal(probesBefore + 2, mProbeCalls);
        Assert.Equal("Running", viewModel.Rows[0].StateText);
        Assert.All(AllCommands(viewModel), command => Assert.True(command.CanExecute, command.Name));
    }

    [Fact]
    public async Task StopAndRestartAskTheControllerForThatRowOnly()
    {
        ComponentsViewModel viewModel = NewViewModel(SaddleRag(), Mongo());

        viewModel.Rows[1].StopCommand.Execute(parameter: null);
        await viewModel.ActionCompletion;
        viewModel.Rows[1].RestartCommand.Execute(parameter: null);
        await viewModel.ActionCompletion;

        await mServices.Received(1).RunAsync(Arg.Is<IReadOnlyList<string>>(names => names.SequenceEqual(new[] { "MongoDB" })),
                                             ServiceAction.Stop,
                                             Arg.Any<CancellationToken>());
        await mServices.Received(1).RunAsync(Arg.Is<IReadOnlyList<string>>(names => names.SequenceEqual(new[] { "MongoDB" })),
                                             ServiceAction.Restart,
                                             Arg.Any<CancellationToken>());
        Assert.Equal("MongoDB restarted.", viewModel.LastActionMessage);
    }

    [Fact]
    public async Task AFailedActionShowsItsMessage()
    {
        mServices.RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<ServiceAction>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(ServiceActionOutcome.Cancelled));
        ComponentsViewModel viewModel = NewViewModel(SaddleRag());

        viewModel.Rows[0].RestartCommand.Execute(parameter: null);
        await viewModel.ActionCompletion;

        Assert.Contains("administrator approval", viewModel.LastActionMessage, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task AnExceptionFromTheControllerIsShownAndReleasesTheCommands()
    {
        mServices.RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<ServiceAction>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromException<ServiceActionOutcome>(new InvalidOperationException("runner exploded")));
        ComponentsViewModel viewModel = NewViewModel(SaddleRag());

        viewModel.Rows[0].StartCommand.Execute(parameter: null);
        await viewModel.ActionCompletion;

        Assert.Contains("runner exploded", viewModel.LastActionMessage, StringComparison.Ordinal);
        Assert.Contains("SaddleRAG", viewModel.LastActionMessage, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.Rows[0].StartCommand.CanExecute(parameter: null));
    }

    [Fact]
    public async Task RestartAllRunsOneElevatedRestartAndShowsTheSharedResultOnce()
    {
        // MongoDB and SaddleRAG are restarted in one elevated call and both rows carry its message;
        // the same line twice in a row says nothing more, so it is shown once.
        ComponentsViewModel viewModel = NewViewModel(Mongo(), SaddleRag());

        Assert.True(viewModel.RestartAllCommand.CanExecute(parameter: null));
        viewModel.RestartAllCommand.Execute(parameter: null);
        await viewModel.ActionCompletion;

        await mServices.Received(1).RunAsync(Arg.Any<IReadOnlyList<string>>(), ServiceAction.Restart, Arg.Any<CancellationToken>());
        Assert.Equal(["MongoDB and SaddleRAG restarted."], viewModel.LastActionMessage.Split('\n'));
    }

    [Fact]
    public async Task RestartAllStillShowsEachDifferentResultOnItsOwnLine()
    {
        ComponentsViewModel viewModel = NewViewModel(Mongo(), SaddleRag(), RemoteOllama());

        viewModel.RestartAllCommand.Execute(parameter: null);
        await viewModel.ActionCompletion;

        string[] lines = viewModel.LastActionMessage.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("another computer", lines[0], StringComparison.Ordinal);
        Assert.Equal("MongoDB and SaddleRAG restarted.", lines[1]);
    }

    [Fact]
    public async Task ACommandPressedWhileBusyIsIgnored()
    {
        TaskCompletionSource<ServiceActionOutcome> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        mServices.RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<ServiceAction>(), Arg.Any<CancellationToken>())
                 .Returns(gate.Task);
        ComponentsViewModel viewModel = NewViewModel(SaddleRag(), Mongo());

        viewModel.Rows[0].StartCommand.Execute(parameter: null);
        Task first = viewModel.ActionCompletion;
        viewModel.Rows[1].StopCommand.Execute(parameter: null);
        viewModel.RestartAllCommand.Execute(parameter: null);
        gate.SetResult(ServiceActionOutcome.Succeeded);
        await first;

        Assert.Same(first, viewModel.ActionCompletion);
        await mServices.Received(1).RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<ServiceAction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void AnActionStartedElsewhereDisablesEveryCommandUntilItEnds()
    {
        ComponentsViewModel viewModel = NewViewModel(SaddleRag(), Mongo());

        viewModel.OtherActionRunning = true;
        Assert.All(AllCommands(viewModel), command => Assert.False(command.CanExecute, command.Name));

        viewModel.OtherActionRunning = false;
        Assert.All(AllCommands(viewModel), command => Assert.True(command.CanExecute, command.Name));
    }

    [Fact]
    public async Task CommandsAnnounceWhenTheirAvailabilityChangesAndTheViewModelAnnouncesWhenAnActionEnds()
    {
        ComponentsViewModel viewModel = NewViewModel(SaddleRag());
        var rowChanges = 0;
        var restartAllChanges = 0;
        var finished = 0;
        viewModel.Rows[0].StartCommand.CanExecuteChanged += (_, _) => rowChanges++;
        viewModel.RestartAllCommand.CanExecuteChanged += (_, _) => restartAllChanges++;
        viewModel.ActionFinished += (_, _) => finished++;

        viewModel.Rows[0].StartCommand.Execute(parameter: null);
        await viewModel.ActionCompletion;

        Assert.Equal(expected: 2, rowChanges);
        Assert.Equal(expected: 2, restartAllChanges);
        Assert.Equal(expected: 1, finished);
    }

    [Fact]
    public async Task AnActionFinishedHandlerThatThrowsDoesNotFaultTheActionOrKeepTheCommandsDisabled()
    {
        RecordingLogger logger = new();
        ComponentsViewModel viewModel = new([SaddleRag()], ProbeAsync, mController, logger: logger);
        viewModel.ActionFinished += (_, _) => throw new InvalidOperationException("menu is gone");

        viewModel.Rows[0].StartCommand.Execute(parameter: null);
        await viewModel.ActionCompletion;

        Assert.True(viewModel.ActionCompletion.IsCompletedSuccessfully);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.Rows[0].StartCommand.CanExecute(parameter: null));
        Assert.Equal([LogLevel.Warning], logger.Levels);
    }

    #endregion

    private sealed class RecordingLogger : ILogger<ComponentsViewModel>
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
}
