// SaddleRagVersion.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Reflection;

#endregion

namespace SaddleRAG.Core;

/// <summary>
///     The one place the running build identifies itself. Directory.Build.props stamps
///     every project in the solution with the same <c>Version</c>, so reading Core's own
///     assembly gives the MCP server, the Monitor shell, and the CLI an identical answer
///     without each of them re-deriving it.
///     <para>
///         <see cref="Informational" /> is the full SemVer string the release workflow
///         derives from the git tag, including any <c>+sha</c> build metadata
///         (e.g. <c>1.11.0+3f49fe5</c>) — that is what a bug report needs.
///         <see cref="Display" /> drops the metadata for surfaces with no room for it.
///         Local dev builds report the Directory.Build.props default so nobody mistakes
///         them for a tagged release.
///     </para>
/// </summary>
public static class SaddleRagVersion
{
    /// <summary>Full stamped version, including any <c>+sha</c> build metadata.</summary>
    public static string Informational { get; } = ReadInformationalVersion();

    /// <summary>Stamped version without build metadata, for constrained display surfaces.</summary>
    public static string Display { get; } = TrimBuildMetadata(Informational);

    private static string ReadInformationalVersion()
    {
        string? stamped = typeof(SaddleRagVersion).Assembly
                                                  .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                                                  ?.InformationalVersion;
        string result = string.IsNullOrWhiteSpace(stamped) ? DefaultDevVersion : stamped;
        return result;
    }

    private static string TrimBuildMetadata(string version)
    {
        int separator = version.IndexOf('+', StringComparison.Ordinal);
        string result = separator < 0 ? version : version[..separator];
        return result;
    }

    /// <summary>Reported when the build left no informational version behind.</summary>
    public const string DefaultDevVersion = "0.0.0-dev";
}
