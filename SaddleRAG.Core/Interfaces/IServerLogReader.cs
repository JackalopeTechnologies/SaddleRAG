// IServerLogReader.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Read-only access to the tail of the newest server log file, for the
///     monitor's Logs page and error badge. Implementations may throw
///     <see cref="IOException" /> / <see cref="UnauthorizedAccessException" />
///     on read failures; callers own the presentation of those.
/// </summary>
public interface IServerLogReader
{
    /// <summary>
    ///     Parse the tail window of the newest log file and return up to
    ///     <paramref name="maxEntries" /> entries, newest first.
    /// </summary>
    ServerLogSnapshot Read(int maxEntries);

    /// <summary>
    ///     Count entries at Error or Fatal level whose timestamp falls within
    ///     the trailing <paramref name="window" /> (per the recovered-failures
    ///     logging contract, recovered incidents log at Warning and are
    ///     therefore not counted).
    /// </summary>
    int CountRecentErrors(TimeSpan window);
}
