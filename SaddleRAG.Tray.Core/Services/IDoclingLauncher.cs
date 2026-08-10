// IDoclingLauncher.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>Starting the user's registered Docling, behind a seam the coordinator can fake.</summary>
public interface IDoclingLauncher
{
    Task<DoclingLaunchOutcome> EnsureRunningAsync(CancellationToken cancellationToken = default);
}
