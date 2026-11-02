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
///     Cancelling the wait, or the wait limit running out, never touches the elevated process,
///     which the tray could not stop anyway and which must finish the service action it began.
/// </summary>
public sealed class RunAsProcessLauncher : IElevatedProcessLauncher
{
    private const string NoProcessMessage = "Windows did not return a process for the elevated script.";
    private const string WaitLimitMessage = "The elevated process did not finish within {0}.";

    public async Task<int> RunAsync(ProcessStartInfo startInfo, TimeSpan waitLimit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        // Process.Start blocks until the operator answers the administrator prompt, so it runs on
        // a pool thread: the tray's UI thread stays responsive. It throws Win32Exception (error
        // 1223) when the prompt is declined; that propagates so the runner reports Cancelled.
        using Process process = await Task.Run(() => Process.Start(startInfo)
                                                     ?? throw new InvalidOperationException(NoProcessMessage),
                                               ct);

        // The limit is counted from here, once the process exists, so time spent at the prompt
        // does not use up the wait for the service action itself.
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(waitLimit);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch(OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(string.Format(WaitLimitMessage, waitLimit));
        }

        return process.ExitCode;
    }
}
