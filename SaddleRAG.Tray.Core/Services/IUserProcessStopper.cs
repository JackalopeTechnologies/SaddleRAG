// IUserProcessStopper.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Stops a process the signed-in user owns. It never elevates: Docling and Ollama run in the
///     user's session, so the tray stops them as the user.
/// </summary>
public interface IUserProcessStopper
{
    /// <summary>
    ///     Kills the process, and everything it started when <paramref name="entireProcessTree" /> is true,
    ///     then waits for it to exit. The process id is only trusted if the process now holding it
    ///     started at <see cref="ListeningProcess.StartTimeUtc" />: an id can be reused after its
    ///     process exits, and a different process is left alone (<see cref="ProcessStopOutcome.NotFound" />).
    ///     A process whose start time is not known cannot be verified and is not touched
    ///     (<see cref="ProcessStopOutcome.AccessDenied" />).
    /// </summary>
    /// <param name="process">The process the operator was shown, as found by the port lookup.</param>
    /// <param name="entireProcessTree">True to stop the processes it started too.</param>
    /// <param name="timeout">How long to wait for the process to exit after it is killed.</param>
    /// <param name="ct">Cancels the wait; the process is still killed.</param>
    Task<ProcessStopOutcome> StopAsync(ListeningProcess process, bool entireProcessTree, TimeSpan timeout, CancellationToken ct);
}