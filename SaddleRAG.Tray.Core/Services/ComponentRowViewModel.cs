// ComponentRowViewModel.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Runtime.CompilerServices;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     One line of the Components window: a component, where it runs, what the last check found, and
///     the three commands. The commands cannot run for a component on another computer, nor while
///     any action on any row is running.
/// </summary>
public sealed class ComponentRowViewModel : INotifyPropertyChanged
{
    internal ComponentRowViewModel(ComponentDefinition definition, ComponentsViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(owner);

        Definition = definition;
        mOwner = owner;
        LocationText = definition.HostedLocally ? ThisComputerText : AnotherComputerText;
        StartCommand = new RelayCommand(() => mOwner.Begin(this, ServiceAction.Start), CanAct);
        StopCommand = new RelayCommand(() => mOwner.Begin(this, ServiceAction.Stop), CanAct);
        RestartCommand = new RelayCommand(() => mOwner.Begin(this, ServiceAction.Restart), CanAct);
    }

    private const string ThisComputerText = "This computer";
    private const string AnotherComputerText = "Another computer";
    private const string CheckingText = "Checking…";
    private const string RunningText = "Running";
    private const string StoppedText = "Stopped";
    private const string UnreachableText = "Not responding";
    private const string NotInstalledText = "Not installed";
    private const string UnknownText = "Unknown";

    private readonly ComponentsViewModel mOwner;
    private ComponentState mState = ComponentState.Unknown;
    private string mStateText = CheckingText;
    private string mDetail = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The component this row describes.</summary>
    public ComponentDefinition Definition { get; }

    public string DisplayName => Definition.DisplayName;

    /// <summary><c>This computer</c> or <c>Another computer</c>.</summary>
    public string LocationText { get; }

    /// <summary>What the last check found; <see cref="ComponentState.Unknown" /> until the first check ends.</summary>
    public ComponentState State
    {
        get => mState;
        private set
        {
            if (mState != value)
            {
                mState = value;
                OnPropertyChanged();
            }
        }
    }

    public string StateText
    {
        get => mStateText;
        private set
        {
            if (mStateText != value)
            {
                mStateText = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>A short reason the operator can read, such as why a running service is not answering.</summary>
    public string Detail
    {
        get => mDetail;
        private set
        {
            if (mDetail != value)
            {
                mDetail = value;
                OnPropertyChanged();
            }
        }
    }

    public RelayCommand StartCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand RestartCommand { get; }

    internal void Apply(ComponentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        State = status.State;
        StateText = status.State switch
                    {
                        ComponentState.Running => RunningText,
                        ComponentState.Stopped => StoppedText,
                        ComponentState.Unreachable => UnreachableText,
                        ComponentState.NotInstalled => NotInstalledText,
                        _ => UnknownText
                    };
        Detail = status.Detail;
    }

    internal void RaiseCommandsChanged()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
    }

    private bool CanAct() => Definition.HostedLocally && mOwner.CanAct;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
