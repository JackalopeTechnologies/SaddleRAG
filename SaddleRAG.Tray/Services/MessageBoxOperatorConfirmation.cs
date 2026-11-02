// MessageBoxOperatorConfirmation.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Windows;
using System.Windows.Threading;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Asks the operator, in a Yes/No message box, before the tray stops the process it found on
///     Docling's or Ollama's port. The default button is No, so a stray Enter never stops anything.
///     The wording is <see cref="StopConfirmationText" />.
///     <para>
///         The box is owned and activated like the tray's other dialogs (<see cref="OwnedMessageBox" />),
///         and is always shown on the WPF dispatcher thread whichever thread asks.
///     </para>
/// </summary>
public sealed class MessageBoxOperatorConfirmation : IOperatorConfirmation
{
    public async Task<bool> ConfirmStopAsync(ComponentDefinition component, ListeningProcess process, bool looksExpected)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(process);

        Dispatcher dispatcher = Application.Current.Dispatcher;
        bool confirmed = dispatcher.CheckAccess()
                             ? Ask(component, process, looksExpected)
                             : await dispatcher.InvokeAsync(() => Ask(component, process, looksExpected));
        return confirmed;
    }

    private static bool Ask(ComponentDefinition component, ListeningProcess process, bool looksExpected)
    {
        MessageBoxResult answer = OwnedMessageBox.Show(StopConfirmationText.Message(component, process, looksExpected),
                                                       StopConfirmationText.Title(component),
                                                       MessageBoxButton.YesNo,
                                                       looksExpected ? MessageBoxImage.Question : MessageBoxImage.Warning,
                                                       MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }
}
