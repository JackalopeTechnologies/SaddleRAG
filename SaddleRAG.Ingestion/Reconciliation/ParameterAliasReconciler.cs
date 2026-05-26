// ParameterAliasReconciler.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Ingestion.Reconciliation;

/// <summary>
///     Reconciles a renamed MCP tool parameter against its deprecated
///     alias. Both arguments are accepted as optional; the new
///     (canonical) name wins when both are present. When only the old
///     name is provided, a deprecation warning is logged at most once
///     per process for that <c>(newName, oldName)</c> pair, gated by
///     <see cref="DeprecationLog" />. When both are provided with
///     different values an <see cref="ArgumentException" /> is thrown
///     so the caller fixes the call site rather than getting silent
///     unintended behavior.
///     <para>
///         Empty strings are treated as "not provided" so MCP tools can
///         keep their existing <c>string libraryId = ""</c> defaults and
///         still discriminate "actually empty" from "absent".
///     </para>
/// </summary>
public static class ParameterAliasReconciler
{
    #region Resolve

    /// <summary>
    ///     Pick the right value for a renamed parameter, applying the
    ///     deprecation policy described in the type summary.
    /// </summary>
    /// <param name="newValue">Value supplied under the canonical name.</param>
    /// <param name="oldValue">Value supplied under the deprecated alias.</param>
    /// <param name="newName">Canonical parameter name (used in messages).</param>
    /// <param name="oldName">Deprecated alias name (used in messages).</param>
    /// <param name="logger">Logger that receives the deprecation warning.</param>
    /// <param name="log">
    ///     Optional <see cref="DeprecationLog" /> override; defaults to
    ///     <see cref="DeprecationLog.Default" />. Tests pass their own
    ///     instance to assert log-once behavior in isolation.
    /// </param>
    public static T? Resolve<T>(T? newValue,
                                T? oldValue,
                                string newName,
                                string oldName,
                                ILogger logger,
                                DeprecationLog? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(newName);
        ArgumentException.ThrowIfNullOrEmpty(oldName);
        ArgumentNullException.ThrowIfNull(logger);

        var dedupe = log ?? DeprecationLog.Default;
        bool hasNew = !IsConsideredEmpty(newValue);
        bool hasOld = !IsConsideredEmpty(oldValue);
        // Object.Equals is null-safe and avoids the null-forgiving
        // operator the analyzer rejects. Boxes value-type T but MCP
        // parameter types are always reference types in practice.
        bool sameValue = hasNew && hasOld && Equals(newValue, oldValue);

        T? result = (hasNew, hasOld, sameValue) switch
            {
                (false, false, _) => default,
                (true, false, _) => newValue,
                (true, true, true) => newValue,
                (false, true, _) => LogAndReturnOld(oldValue, newName, oldName, logger, dedupe),
                (true, true, false) => throw new ArgumentException(BuildConflictMessage(newName, oldName))
            };
        return result;
    }

    #endregion

    private static T? LogAndReturnOld<T>(T? value,
                                         string newName,
                                         string oldName,
                                         ILogger logger,
                                         DeprecationLog dedupe)
    {
        string key = $"{newName}/{oldName}";
        if (dedupe.ShouldLog(key))
            logger.LogWarning(DeprecationLogMessageTemplate, oldName, newName);
        return value;
    }

    private static bool IsConsideredEmpty<T>(T? value) => value switch
        {
            null => true,
            string s => string.IsNullOrEmpty(s),
            _ => false
        };

    private static string BuildConflictMessage(string newName, string oldName)
        => $"Parameters '{newName}' and '{oldName}' were both provided with different values. " +
           $"Pass '{newName}' only ('{oldName}' is a deprecated alias).";

    private const string DeprecationLogMessageTemplate =
        "MCP parameter '{Old}' is deprecated; use '{New}' instead. The deprecated alias will be removed in the next release.";
}
