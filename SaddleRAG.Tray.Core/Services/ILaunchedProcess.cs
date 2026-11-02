// ILaunchedProcess.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     A handle from <see cref="IProcessStarter.Start" /> that can say whether its process has
///     exited. A real start returns a <see cref="System.Diagnostics.Process" />, which the launcher
///     asks directly; this lets a stand-in handle answer the same question in tests.
/// </summary>
public interface ILaunchedProcess : IDisposable
{
    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }
}
