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
    /// <summary>Kills the process and everything it started, then waits for it to exit.</summary>
    /// <param name="processId">The process to stop.</param>
    /// <param name="timeout">How long to wait for the process to exit after it is killed.</param>
    /// <param name="ct">Cancels the wait; the process is still killed.</param>
    Task<ProcessStopOutcome> StopAsync(int processId, TimeSpan timeout, CancellationToken ct);
}