// IDoclingLauncher.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>Starting the user's registered Docling, behind a seam the coordinator can fake.</summary>
public interface IDoclingLauncher
{
    Task<DoclingLaunchOutcome> EnsureRunningAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     True only when this launcher started a Docling process that has not exited. Tells a Docling
    ///     that crashed after launch (exited) from one still loading (running).
    /// </summary>
    bool LaunchedProcessIsRunning { get; }

    /// <summary>
    ///     Lets the next <see cref="EnsureRunningAsync" /> start Docling again in this session. Called
    ///     only on an operator start or restart, when nothing listens on Docling's port and
    ///     <see cref="LaunchedProcessIsRunning" /> is false.
    /// </summary>
    void AllowRelaunchAfterOperatorStop();
}
