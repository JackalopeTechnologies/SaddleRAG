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
    public async Task<ProcessStopOutcome> StopAsync(ListeningProcess process,
                                                    bool entireProcessTree,
                                                    TimeSpan timeout,
                                                    CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(process.ProcessId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, smMaxTimeout);
        if (process.ProcessId == Environment.ProcessId)
        {
            // Killing the process tree of the tray itself would end the tray mid-action.
            throw new ArgumentException(OwnProcessMessage, nameof(process));
        }

        ProcessStopOutcome result;
        if (process.StartTimeUtc is not { } expectedStartUtc)
        {
            // Without a start time the process cannot be told from a later process that reuses its
            // id. A process whose start time Windows hides from this user is one it would refuse to
            // let this user kill anyway.
            result = ProcessStopOutcome.AccessDenied;
        }
        else
        {
            Process? live = FindProcess(process.ProcessId);
            if (live is null)
            {
                result = ProcessStopOutcome.NotFound;
            }
            else
            {
                using(live)
                    result = await KillAndWaitAsync(live, expectedStartUtc, entireProcessTree, timeout, ct);
            }
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
                                                                    DateTime expectedStartUtc,
                                                                    bool entireProcessTree,
                                                                    TimeSpan timeout,
                                                                    CancellationToken ct)
    {
        ProcessStopOutcome result;
        try
        {
            // Open the process handle once, before the identity check. Process caches it, so the start
            // time read and the Kill below go through this one handle, and the handle keeps the process
            // object alive: its id cannot pass to another process between the check and the kill.
            // Without it, each call opens a handle of its own by process id. A handle that cannot be
            // opened fails here exactly as the start time read would (access denied, or already exited).
            _ = process.SafeHandle;
            if (process.StartTime.ToUniversalTime() == expectedStartUtc)
            {
                process.Kill(entireProcessTree);
                result = await WaitForExitAsync(process, timeout, ct);
            }
            else
            {
                // The process the operator was shown has exited and Windows gave its id to another
                // process. That one is not ours to stop.
                result = ProcessStopOutcome.NotFound;
            }
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