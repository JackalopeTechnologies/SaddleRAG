// IProcessStarter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Diagnostics;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Starting the registered command, behind a seam so launch decisions can be
///     asserted in test without spawning a real Python process.
/// </summary>
public interface IProcessStarter
{
    /// <summary>Starts <paramref name="startInfo" />, returning a handle to dispose, or null.</summary>
    IDisposable? Start(ProcessStartInfo startInfo);
}
