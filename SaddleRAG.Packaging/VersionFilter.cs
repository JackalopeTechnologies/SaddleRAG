// VersionFilter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Linq;

#endregion

namespace SaddleRAG.Packaging;

/// <summary>
///     Selector that turns an MCP-tool-style "versions" argument into a
///     concrete list of versions to export. Supports the three forms
///     mentioned in the spec: "current" (default), "all", and an explicit
///     string list.
/// </summary>
public sealed record VersionFilter
{
    private const string CurrentSelector = "current";
    private const string AllSelector = "all";
    /// <summary>
    ///     The resolution mode for this filter.
    /// </summary>
    public required VersionFilterKind Kind { get; init; }

    /// <summary>
    ///     The explicitly requested versions when <see cref="Kind"/> is
    ///     <see cref="VersionFilterKind.Explicit"/>; empty otherwise.
    /// </summary>
    public IReadOnlyList<string> ExplicitVersions { get; init; } = [];

    /// <summary>
    ///     Singleton for the <see cref="VersionFilterKind.Current"/> mode.
    /// </summary>
    public static VersionFilter Current { get; } = new VersionFilter { Kind = VersionFilterKind.Current };

    /// <summary>
    ///     Singleton for the <see cref="VersionFilterKind.All"/> mode.
    /// </summary>
    public static VersionFilter All { get; } = new VersionFilter { Kind = VersionFilterKind.All };

    /// <summary>
    ///     Parses a string selector ("current" or "all") into a <see cref="VersionFilter"/>.
    /// </summary>
    public static VersionFilter Parse(string? value)
    {
        var trimmed = (value ?? CurrentSelector).Trim().ToLowerInvariant();
        VersionFilter result = trimmed switch
            {
                CurrentSelector or "" => Current,
                AllSelector => All,
                var _ => throw new ArgumentException(
                             $"Unrecognized version selector '{value}'; use '{CurrentSelector}', '{AllSelector}', or a list",
                             nameof(value))
            };
        return result;
    }

    /// <summary>
    ///     Creates an <see cref="VersionFilterKind.Explicit"/> filter from a non-empty list of version strings.
    /// </summary>
    public static VersionFilter Parse(IReadOnlyList<string> explicitVersions)
    {
        ArgumentNullException.ThrowIfNull(explicitVersions);
        if (explicitVersions.Count == 0)
            throw new ArgumentException(
                "Explicit version list is empty; use 'current' or 'all' instead",
                nameof(explicitVersions));
        return new VersionFilter
               {
                   Kind = VersionFilterKind.Explicit,
                   ExplicitVersions = [.. explicitVersions]
               };
    }

    /// <summary>
    ///     Resolves this filter to a concrete list of version strings from the available set.
    ///     Fails fast for <see cref="VersionFilterKind.Explicit"/> if any requested version is absent.
    /// </summary>
    public IReadOnlyList<string> Resolve(string currentVersion, IReadOnlyList<string> availableVersions)
    {
        ArgumentException.ThrowIfNullOrEmpty(currentVersion);
        ArgumentNullException.ThrowIfNull(availableVersions);

        IReadOnlyList<string> result = Kind switch
            {
                VersionFilterKind.Current => [currentVersion],
                VersionFilterKind.All => availableVersions,
                VersionFilterKind.Explicit => ResolveExplicit(availableVersions),
                var _ => throw new InvalidOperationException($"Unhandled kind {Kind}")
            };
        return result;
    }

    private IReadOnlyList<string> ResolveExplicit(IReadOnlyList<string> availableVersions)
    {
        var missing = ExplicitVersions.Where(v => !availableVersions.Contains(v)).ToList();
        if (missing.Count > 0)
            throw new ArgumentException(
                $"Requested versions not present in library: {string.Join(", ", missing)}");
        return ExplicitVersions;
    }
}
