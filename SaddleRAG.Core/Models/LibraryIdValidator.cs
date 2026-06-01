// LibraryIdValidator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.RegularExpressions;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     Validates library identifiers used as collection-name keys in MongoDB and
///     as directory components inside bundle zip entries. Called by both
///     <c>start_ingest</c> and the bundle importer so the rules stay in one place.
/// </summary>
public static partial class LibraryIdValidator
{
    private const string SystemDotPrefix = "system.";

    /// <summary>
    ///     Throws <see cref="ArgumentException" /> if <paramref name="id" /> is not
    ///     a valid library identifier. Valid ids:
    ///     <list type="bullet">
    ///         <item>Start with a lowercase letter or digit.</item>
    ///         <item>Contain only lowercase letters, digits, dots, hyphens, and underscores.</item>
    ///         <item>Do not contain path-traversal sequences (<c>..</c>, <c>/</c>, <c>\</c>).</item>
    ///         <item>Do not contain MongoDB collection-name-illegal chars (<c>$</c>).</item>
    ///         <item>Do not begin with <c>system.</c>.</item>
    ///     </list>
    /// </summary>
    public static void ValidateLibraryId(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (!smValidIdRegex.IsMatch(id))
            throw new ArgumentException(
                $"Invalid library id '{id}': must match [a-z0-9][a-z0-9._-]* (no path traversal, no Mongo-illegal chars).",
                nameof(id));
        if (id.StartsWith(SystemDotPrefix, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Invalid library id '{id}': ids may not begin with '{SystemDotPrefix}'.",
                nameof(id));
    }

    /// <summary>
    ///     Returns true if <paramref name="id" /> passes <see cref="ValidateLibraryId" />
    ///     without throwing.
    /// </summary>
    public static bool IsValidLibraryId(string? id)
    {
        bool res = false;
        if (!string.IsNullOrEmpty(id) && smValidIdRegex.IsMatch(id) && !id.StartsWith(SystemDotPrefix, StringComparison.Ordinal))
            res = true;
        return res;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]*$", RegexOptions.None)]
    private static partial Regex SmValidIdRegex();

    private static readonly Regex smValidIdRegex = SmValidIdRegex();
}
