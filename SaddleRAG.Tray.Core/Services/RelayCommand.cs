// RelayCommand.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Windows.Input;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Minimal <see cref="ICommand" /> so a tray gesture that has no Click event —
///     the taskbar icon's double-click — can run the same action a menu item does. A command given a
///     <c>canExecute</c> test reports it to WPF, which asks again whenever the owner calls
///     <see cref="RaiseCanExecuteChanged" />.
/// </summary>
public sealed class RelayCommand : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);

        mExecute = execute;
        mCanExecute = canExecute;
    }

    private readonly Action mExecute;
    private readonly Func<bool>? mCanExecute;

    /// <summary>Raised by <see cref="RaiseCanExecuteChanged" />; a command with no test never raises it.</summary>
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => mCanExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => mExecute();

    /// <summary>Tells WPF to ask <see cref="CanExecute" /> again. Call it from the thread that owns the control.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
