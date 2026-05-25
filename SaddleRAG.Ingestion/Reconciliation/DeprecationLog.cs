// DeprecationLog.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Ingestion.Reconciliation;

/// <summary>
///     Process-wide dedupe for MCP parameter deprecation warnings. A given
///     deprecation key (e.g. <c>scrape_docs.libraryId</c>) logs at most
///     once per process so a chatty MCP client cannot spam the log. The
///     <see cref="Default" /> instance is shared by every MCP tool that
///     reconciles a renamed parameter against its deprecated alias via
///     <see cref="ParameterAliasReconciler.Resolve{T}" />. Tests construct
///     their own instance and pass it in to assert log-once semantics in
///     isolation.
/// </summary>
public sealed class DeprecationLog
{
    #region Default singleton

    /// <summary>
    ///     Process-wide instance used by MCP tools. Tests should
    ///     construct their own instance instead of mutating this one.
    /// </summary>
    public static DeprecationLog Default { get; } = new DeprecationLog();

    #endregion

    /// <summary>
    ///     Record that the deprecation <paramref name="key" /> has been
    ///     observed. Returns <c>true</c> the first time the key is seen
    ///     and <c>false</c> on every subsequent call with the same key,
    ///     so callers can decide whether to emit a log line. Thread-safe.
    /// </summary>
    /// <param name="key">
    ///     A stable identifier for the deprecation event, conventionally
    ///     <c>"&lt;toolName&gt;.&lt;oldParameterName&gt;"</c>.
    /// </param>
    public bool ShouldLog(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool result;
        lock(mLock)
        {
            result = mSeen.Add(key);
        }
        return result;
    }

    /// <summary>
    ///     Reset the set of observed keys. Test-only entry point exposed
    ///     to <c>SaddleRAG.Tests</c> via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal void Clear()
    {
        lock(mLock)
        {
            mSeen.Clear();
        }
    }

    private readonly HashSet<string> mSeen = new HashSet<string>(StringComparer.Ordinal);

    private readonly object mLock = new object();
}
