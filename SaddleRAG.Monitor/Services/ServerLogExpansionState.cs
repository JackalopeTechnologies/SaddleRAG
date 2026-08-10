// ServerLogExpansionState.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Tracks which log rows the user has expanded.
///     <para>
///         Keyed by the log line's own content rather than by object identity: the page
///         re-reads the log file on a timer and every read produces fresh
///         <see cref="ServerLogEntry" /> instances, so an identity-keyed set lost the user's
///         expansion on the next tick and the row closed under them. Record equality would
///         not have helped either — <see cref="ServerLogEntry.DetailLines" /> is a list, which
///         compares by reference.
///     </para>
/// </summary>
public sealed class ServerLogExpansionState
{
    private readonly HashSet<string> mExpanded = new(StringComparer.Ordinal);

    /// <summary>How many rows are currently expanded.</summary>
    public int ExpandedCount => mExpanded.Count;

    public bool IsExpanded(ServerLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return mExpanded.Contains(KeyFor(entry));
    }

    public void Toggle(ServerLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string key = KeyFor(entry);
        if (!mExpanded.Remove(key))
            mExpanded.Add(key);
    }

    public void CollapseAll() => mExpanded.Clear();

    /// <summary>
    ///     Identity of a log line as the user perceives it. Two genuinely identical lines at the
    ///     same instant expand together, which is a fair reading of "the same entry" and far
    ///     better than the row closing on its own.
    /// </summary>
    private static string KeyFor(ServerLogEntry entry) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
                      $"{entry.Timestamp.UtcTicks}|{entry.Level}|{entry.Message}");
}
