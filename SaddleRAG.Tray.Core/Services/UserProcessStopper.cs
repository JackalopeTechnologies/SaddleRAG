// UserProcessStopper.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Stops a process the signed-in user owns by killing it and everything it started. A process
///     that belongs to another user or is elevated is reported as <see cref="ProcessStopOutcome.AccessDenied" />;
///     nothing here ever elevates.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UserProcessStopper : IUserProcessStopper
{
    public async Task<ProcessStopOutcome> StopAsync(int processId, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, smMaxTimeout);
        if (processId == Environment.ProcessId)
        {
            // Killing the process tree of the tray itself would end the tray mid-action.
            throw new ArgumentException(OwnProcessMessage, nameof(processId));
        }

        ProcessStopOutcome result;
        Process? process = FindProcess(processId);
        if (process is null)
        {
            result = ProcessStopOutcome.NotFound;
        }
        else
        {
            using(process)
                result = await KillAndWaitAsync(process, timeout, ct);
        }

        return result;
    }

    private static Process? FindProcess(int processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch(ArgumentException)
        {
            // GetProcessById reports "not running" as an ArgumentException; that is the NotFound outcome.
            process = null;
        }

        return process;
    }

    private static async Task<ProcessStopOutcome> KillAndWaitAsync(Process process,
                                                                    TimeSpan timeout,
                                                                    CancellationToken ct)
    {
        ProcessStopOutcome result;
        try
        {
            process.Kill(entireProcessTree: true);
            result = await WaitForExitAsync(process, timeout, ct);
        }
        catch(Exception ex) when (ex is Win32Exception or UnauthorizedAccessException
                                      || ex is AggregateException aggregate && AllDenied(aggregate))
        {
            result = ProcessStopOutcome.AccessDenied;
        }
        catch(InvalidOperationException)
        {
            // Kill and the exit wait report "already exited" this way: the process left first.
            result = ProcessStopOutcome.NotFound;
        }

        return result;
    }

    private static async Task<ProcessStopOutcome> WaitForExitAsync(Process process,
                                                                   TimeSpan timeout,
                                                                   CancellationToken ct)
    {
        ProcessStopOutcome result;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            result = ProcessStopOutcome.Stopped;
        }
        catch(OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The caller did not cancel, so the time allowed ran out.
            result = ProcessStopOutcome.TimedOut;
        }

        return result;
    }

    // Killing a whole tree reports each process it could not end inside an AggregateException.
    private static bool AllDenied(AggregateException aggregate) =>
        aggregate.InnerExceptions.All(inner => inner is Win32Exception or UnauthorizedAccessException);

    // The longest CancellationTokenSource.CancelAfter accepts, in milliseconds.
    private const long MaxTimeoutMilliseconds = 4294967294;
    private static readonly TimeSpan smMaxTimeout = TimeSpan.FromMilliseconds(MaxTimeoutMilliseconds);
    private const string OwnProcessMessage = "The tray will not stop its own process.";
}