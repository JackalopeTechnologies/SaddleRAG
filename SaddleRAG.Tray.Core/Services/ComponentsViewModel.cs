// ComponentsViewModel.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     What the Components window shows and does: one row per component, a Restart all command, and a
///     status line. It never acts on its own; every action starts from a command the operator pressed.
///     <list type="bullet">
///         <item>
///             Only one action runs at a time, across every row and Restart all. While one runs, or
///             while another part of the tray holds the elevated service runner
///             (<see cref="OtherActionRunning" />), every command is disabled.
///         </item>
///         <item>
///             When an action ends its message appears in <see cref="LastActionMessage" />, the rows
///             are checked again, and only then do the commands come back.
///         </item>
///         <item>
///             A check that fails leaves its row <see cref="ComponentState.Unknown" /> with the reason
///             in its detail and never stops the other rows from refreshing.
///         </item>
///     </list>
///     <para>
///         All members are used from the WPF dispatcher thread. Awaits keep that thread, so property
///         changes reach the window without marshalling.
///     </para>
/// </summary>
public sealed class ComponentsViewModel : INotifyPropertyChanged
{
    /// <param name="components">The rows to show, in display order.</param>
    /// <param name="probe">Checks one component; normally <see cref="ComponentStatusProbe.ProbeAsync" />.</param>
    /// <param name="controller">Carries out the operator's start, stop and restart.</param>
    /// <param name="time">The clock for the "last checked" text; defaults to the system clock.</param>
    /// <param name="shutdown">Cancelled when the tray exits; ends an action's wait and any refresh.</param>
    public ComponentsViewModel(IReadOnlyList<ComponentDefinition> components,
                               Func<ComponentDefinition, CancellationToken, Task<ComponentStatus>> probe,
                               ComponentController controller,
                               TimeProvider? time = null,
                               CancellationToken shutdown = default)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(controller);

        mComponents = [.. components];
        mProbe = probe;
        mController = controller;
        mTime = time ?? TimeProvider.System;
        mShutdown = shutdown;
        Rows = [.. mComponents.Select(component => new ComponentRowViewModel(component, this))];
        RestartAllCommand = new RelayCommand(BeginRestartAll, () => CanAct);
    }

    private const string LineSeparator = "\n";
    private const string LastCheckedFormat = "Last checked {0}";
    private const string ClockFormat = "HH:mm:ss";
    private const string CheckFailedPrefix = "Could not check: ";
    private const string FailedSeparator = " failed: ";
    private const string WorkingSuffix = "…";
    private const string StartingSubject = "Starting ";
    private const string StoppingSubject = "Stopping ";
    private const string RestartingSubject = "Restarting ";
    private const string RestartAllSubject = "Restarting all components";

    private readonly IReadOnlyList<ComponentDefinition> mComponents;
    private readonly Func<ComponentDefinition, CancellationToken, Task<ComponentStatus>> mProbe;
    private readonly ComponentController mController;
    private readonly TimeProvider mTime;
    private readonly CancellationToken mShutdown;

    // Refreshes run one at a time so a slow timer refresh can never overwrite the newer rows an
    // action's own refresh has just shown. Only WaitAsync and Release are used, so there is no
    // kernel handle to dispose.
    private readonly SemaphoreSlim mRefreshGate = new(initialCount: 1, maxCount: 1);

    private bool mIsBusy;
    private bool mOtherActionRunning;
    private string mLastActionMessage = string.Empty;
    private string mLastCheckedText = string.Empty;
    private Task mActionTask = Task.CompletedTask;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after an operator action has ended and the rows were checked again.</summary>
    public event EventHandler? ActionFinished;

    /// <summary>The rows, in the order of the components given.</summary>
    public ObservableCollection<ComponentRowViewModel> Rows { get; }

    /// <summary>Restarts every local component in dependency order.</summary>
    public RelayCommand RestartAllCommand { get; }

    /// <summary>True while an operator action started here is running.</summary>
    public bool IsBusy
    {
        get => mIsBusy;
        private set
        {
            if (mIsBusy != value)
            {
                mIsBusy = value;
                OnPropertyChanged();
                RaiseCommandsChanged();
            }
        }
    }

    /// <summary>
    ///     Set by the tray while it runs a Start or Stop of the MCP service through the same elevated
    ///     runner, so two administrator prompts for the same service never stack up.
    /// </summary>
    public bool OtherActionRunning
    {
        get => mOtherActionRunning;
        set
        {
            if (mOtherActionRunning != value)
            {
                mOtherActionRunning = value;
                OnPropertyChanged();
                RaiseCommandsChanged();
            }
        }
    }

    /// <summary>What the last action did, or what the running one is doing. Several results are one per line.</summary>
    public string LastActionMessage
    {
        get => mLastActionMessage;
        private set
        {
            if (mLastActionMessage != value)
            {
                mLastActionMessage = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>When the rows were last checked; empty until the first check ends.</summary>
    public string LastCheckedText
    {
        get => mLastCheckedText;
        private set
        {
            if (mLastCheckedText != value)
            {
                mLastCheckedText = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    ///     Ends when the most recently started action, and the check that follows it, have finished.
    ///     It never faults: a failure becomes the status message. Callers that must wait for an action
    ///     (tests) await it.
    /// </summary>
    public Task ActionCompletion => mActionTask;

    internal bool CanAct => !mIsBusy && !mOtherActionRunning;

    /// <summary>
    ///     Checks every row, all at once, and shows what each found. A check that throws leaves its row
    ///     Unknown with the reason. Refreshes queue behind one another.
    /// </summary>
    /// <param name="ct">Cancels the refresh; the cancellation propagates.</param>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await mRefreshGate.WaitAsync(ct);
        try
        {
            ComponentStatus[] statuses = await Task.WhenAll(mComponents.Select(component => ProbeAsync(component, ct)));
            for (var index = 0; index < statuses.Length; index++)
                Rows[index].Apply(statuses[index]);

            LastCheckedText = string.Format(LastCheckedFormat,
                                            mTime.GetLocalNow().ToString(ClockFormat, CultureInfo.InvariantCulture));
        }
        finally
        {
            mRefreshGate.Release();
        }
    }

    internal void Begin(ComponentRowViewModel row, ServiceAction action)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (CanAct && row.Definition.HostedLocally)
        {
            ComponentDefinition component = row.Definition;
            string subject = action switch
                             {
                                 ServiceAction.Start => StartingSubject,
                                 ServiceAction.Stop => StoppingSubject,
                                 _ => RestartingSubject
                             } + component.DisplayName;
            mActionTask = RunExclusiveAsync(subject, async ct => (await RunRowActionAsync(component, action, ct)).Message);
        }
    }

    private void BeginRestartAll()
    {
        if (CanAct)
            mActionTask = RunExclusiveAsync(RestartAllSubject, RestartAllAsync);
    }

    private Task<ComponentActionResult> RunRowActionAsync(ComponentDefinition component,
                                                          ServiceAction action,
                                                          CancellationToken ct) =>
        action switch
        {
            ServiceAction.Start => mController.StartAsync(component, ct),
            ServiceAction.Stop => mController.StopAsync(component, ct),
            _ => mController.RestartAsync(component, ct)
        };

    private async Task<string> RestartAllAsync(CancellationToken ct)
    {
        IReadOnlyList<ComponentActionResult> results = await mController.RestartAllAsync(mComponents, ct);
        return string.Join(LineSeparator, results.Select(result => result.Message));
    }

    private async Task RunExclusiveAsync(string subject, Func<CancellationToken, Task<string>> run)
    {
        IsBusy = true;
        LastActionMessage = subject + WorkingSuffix;
        try
        {
            LastActionMessage = await ShowFailureAsync(subject, run);
            await RefreshAfterActionAsync();
        }
        finally
        {
            IsBusy = false;
            ActionFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    // The controller reports every expected outcome as a result. An exception reaching here is
    // unexpected (the controller has already logged it); the operator still has to see that the
    // action did not finish, and the commands have to come back.
    private async Task<string> ShowFailureAsync(string subject, Func<CancellationToken, Task<string>> run)
    {
        string message;
        try
        {
            message = await run(mShutdown);
        }
        catch(Exception ex)
        {
            message = $"{subject}{FailedSeparator}{ex.Message}";
        }

        return message;
    }

    private async Task RefreshAfterActionAsync()
    {
        try
        {
            await RefreshAsync(mShutdown);
        }
        catch(OperationCanceledException) when (mShutdown.IsCancellationRequested)
        {
            // The tray is exiting; there is no window left to show the rows to.
        }
    }

    private async Task<ComponentStatus> ProbeAsync(ComponentDefinition component, CancellationToken ct)
    {
        ComponentStatus result;
        try
        {
            result = await mProbe(component, ct);
        }
        catch(Exception ex) when (!ct.IsCancellationRequested)
        {
            // One component's failed check must not hide the others; the reason is on the row.
            result = new ComponentStatus(component.Kind, ComponentState.Unknown, $"{CheckFailedPrefix}{ex.Message}");
        }

        return result;
    }

    private void RaiseCommandsChanged()
    {
        RestartAllCommand.RaiseCanExecuteChanged();
        foreach (ComponentRowViewModel row in Rows)
            row.RaiseCommandsChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
