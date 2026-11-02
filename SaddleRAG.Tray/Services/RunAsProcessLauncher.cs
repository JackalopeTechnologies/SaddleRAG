// RunAsProcessLauncher.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Diagnostics;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     The real <see cref="IElevatedProcessLauncher" />: starts the process through the Windows
///     administrator prompt (the start info carries the <c>runas</c> verb) and waits for it to end.
///     Cancelling the wait never touches the elevated process, which the tray could not stop anyway
///     and which must finish the service action it began.
/// </summary>
public sealed class RunAsProcessLauncher : IElevatedProcessLauncher
{
    private const string NoProcessMessage = "Windows did not return a process for the elevated script.";

    public async Task<int> RunAsync(ProcessStartInfo startInfo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        // Process.Start throws Win32Exception (error 1223) when the operator declines the prompt;
        // that is deliberately left to propagate so the runner can report it as Cancelled.
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException(NoProcessMessage);
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}
