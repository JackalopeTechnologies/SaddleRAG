// OwnedMessageBox.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Windows;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Shows a message box from a tray app that may have no window of its own. An ownerless
///     MessageBox opens unactivated, and the closing context menu's input dismisses it before the
///     user can read it. This gives it a tiny, off-screen, topmost owner window that is activated
///     first, so the dialog stays modal and on top until the user answers. The owner is closed in
///     finally. Must be called on the WPF dispatcher thread.
/// </summary>
public static class OwnedMessageBox
{
    public static MessageBoxResult Show(string message,
                                        string title,
                                        MessageBoxButton buttons,
                                        MessageBoxImage image,
                                        MessageBoxResult defaultResult)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(title);

        Window owner = new()
                           {
                               Width = 1,
                               Height = 1,
                               Left = OffScreenCoordinate,
                               Top = OffScreenCoordinate,
                               ShowInTaskbar = false,
                               WindowStyle = WindowStyle.None,
                               Topmost = true,
                               ShowActivated = true
                           };
        MessageBoxResult result;
        try
        {
            owner.Show();
            result = MessageBox.Show(owner, message, title, buttons, image, defaultResult);
        }
        finally
        {
            owner.Close();
        }

        return result;
    }

    private const int OffScreenCoordinate = -10000;
}
