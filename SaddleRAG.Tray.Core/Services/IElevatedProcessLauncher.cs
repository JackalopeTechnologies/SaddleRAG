// IElevatedProcessLauncher.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Starts a process through the Windows administrator-approval prompt and waits for it to end,
///     behind a seam so the runner can be tested without ever raising a real prompt.
/// </summary>
public interface IElevatedProcessLauncher
{
    /// <summary>
    ///     Runs <paramref name="startInfo" /> and returns the process exit code. Throws
    ///     <see cref="System.ComponentModel.Win32Exception" /> exactly as <c>Process.Start</c> does
    ///     (error 1223 when the operator declines the prompt).
    ///     <para>
    ///         <paramref name="waitLimit" /> is the longest to wait for the process to end, counted
    ///         from when it has started -- not from when the prompt appeared, which the operator
    ///         controls. When it runs out the wait stops with <see cref="TimeoutException" />.
    ///         Starting must not block the calling (UI) thread while the prompt is up.
    ///     </para>
    ///     <para>
    ///         When <paramref name="ct" /> is cancelled the wait stops with
    ///         <see cref="OperationCanceledException" />. In both cases the elevated process is not
    ///         touched and finishes its work.
    ///     </para>
    /// </summary>
    Task<int> RunAsync(System.Diagnostics.ProcessStartInfo startInfo, TimeSpan waitLimit, CancellationToken ct);
}
