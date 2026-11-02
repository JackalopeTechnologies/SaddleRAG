// StopConfirmationText.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     The wording of the question the tray asks before it stops the process it found on Docling's or
///     Ollama's port. It names the component and the process, and says plainly whether the processes
///     that process started go with it.
/// </summary>
public static class StopConfirmationText
{
    /// <summary>The dialog title.</summary>
    /// <param name="component">The component the operator asked to stop or restart.</param>
    public static string Title(ComponentDefinition component)
    {
        ArgumentNullException.ThrowIfNull(component);

        return string.Format(TitleFormat, component.DisplayName);
    }

    /// <summary>The dialog body.</summary>
    /// <param name="component">The component the operator asked to stop or restart.</param>
    /// <param name="process">The process found listening on the component's port.</param>
    /// <param name="looksExpected">
    ///     True when the process name is one that component normally runs as. False adds a warning and
    ///     says that only this process, not what it started, will be stopped.
    /// </param>
    public static string Message(ComponentDefinition component, ListeningProcess process, bool looksExpected)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(process);

        string scope = looksExpected
                           ? ExpectedScope
                           : string.Format(UnexpectedFormat, component.DisplayName);
        string result = string.Join(LineBreak,
                                    string.Format(QuestionFormat, component.DisplayName),
                                    string.Empty,
                                    string.Format(ProcessFormat, process.ProcessName),
                                    string.Format(ProcessIdFormat, process.ProcessId),
                                    string.Format(ProgramFormat, process.ExecutablePath ?? PathUnavailable),
                                    string.Empty,
                                    scope);
        return result;
    }

    private const string LineBreak = "\n";
    private const string TitleFormat = "Stop {0}?";
    private const string QuestionFormat = "Stop {0}? The tray found this process listening for it:";
    private const string ProcessFormat = "Process: {0}";
    private const string ProcessIdFormat = "Process ID: {0}";
    private const string ProgramFormat = "Program: {0}";
    private const string PathUnavailable = "path unavailable";
    private const string ExpectedScope = "This process and the processes it started will be stopped.";

    private const string UnexpectedFormat =
        "This process does not look like {0}. Only this process will be stopped, not its child processes.";
}
