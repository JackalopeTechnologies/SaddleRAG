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
    ///     True when a Docling command is registered. A restart checks this before stopping anything,
    ///     so it never stops a Docling it could not start again.
    /// </summary>
    bool IsRegistered { get; }

    /// <summary>
    ///     Lets the next <see cref="EnsureRunningAsync" /> start Docling again in this session, but only
    ///     when the process this launcher started has exited and <paramref name="portIsFree" /> returns
    ///     true. The check and the clear are one atomic step with respect to a launch. Called only on an
    ///     operator start or restart.
    /// </summary>
    /// <param name="portIsFree">Reports whether nothing listens on Docling's port.</param>
    /// <returns>True when the guard was cleared.</returns>
    bool TryAllowRelaunch(Func<bool> portIsFree);
}
