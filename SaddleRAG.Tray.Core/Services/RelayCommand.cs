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
///     the taskbar icon's double-click — can run the same action a menu item does.
/// </summary>
public sealed class RelayCommand : ICommand
{
    public RelayCommand(Action execute)
    {
        ArgumentNullException.ThrowIfNull(execute);

        mExecute = execute;
    }

    private readonly Action mExecute;

    /// <summary>
    ///     Accepts subscribers and never raises: this command is unconditionally executable,
    ///     so there is no state change for WPF to re-query.
    /// </summary>
    event EventHandler? ICommand.CanExecuteChanged
    {
        add
        {
        }
        remove
        {
        }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => mExecute();
}
