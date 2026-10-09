// DatabaseFailFastScope.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Database;

/// <summary>
///     Marks the asynchronous flow of one MCP tool call. Only inside it may a
///     database operation fail fast during an established outage (see
///     <see cref="FailFastServerSelector" />). Everything else — crawls, startup,
///     Monitor pages, background jobs — keeps the driver's normal wait, because
///     those loops rely on that wait to pause while the database is down.
///     <para>
///         The scope travels with the call's execution context, so work the call
///         starts with <c>Task.Run</c> captures it too. Disposing ends it for every
///         copy, so background work that outlives the call stops being eligible the
///         moment the call returns.
///     </para>
/// </summary>
public sealed class DatabaseFailFastScope : IDisposable
{
    private DatabaseFailFastScope(DatabaseFailFastScope? previous)
    {
        mPrevious = previous;
    }

    private readonly DatabaseFailFastScope? mPrevious;
    private volatile bool mEnded;

    /// <summary>
    ///     True when the current flow belongs to a tool call whose scope has not ended.
    /// </summary>
    internal static bool IsActive => smCurrent.Value is { mEnded: false };

    /// <summary>
    ///     Starts a scope for the current flow; dispose it when the tool call ends.
    /// </summary>
    public static DatabaseFailFastScope Begin()
    {
        var scope = new DatabaseFailFastScope(smCurrent.Value);
        smCurrent.Value = scope;
        return scope;
    }

    public void Dispose()
    {
        mEnded = true;
        smCurrent.Value = mPrevious;
    }

    private static readonly AsyncLocal<DatabaseFailFastScope?> smCurrent = new AsyncLocal<DatabaseFailFastScope?>();
}
