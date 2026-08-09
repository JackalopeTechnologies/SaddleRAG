// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Defines filesystem-aware relative-path identity for one directory scan.</summary>
internal sealed class DirectoryPathIdentity
{
    internal DirectoryPathIdentity(bool isCaseSensitive)
    {
        IsCaseSensitive = isCaseSensitive;
        Comparer = isCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        Comparison = isCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }

    internal StringComparer Comparer { get; }

    internal StringComparison Comparison { get; }

    internal bool IsCaseSensitive { get; }

    internal string NormalizeRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string normalized = relativePath.Replace('\\', '/');
        string result = IsCaseSensitive ? normalized : normalized.ToLowerInvariant();
        return result;
    }

    internal bool IsContained(string canonicalRoot, string candidate)
    {
        ArgumentException.ThrowIfNullOrEmpty(canonicalRoot);
        ArgumentException.ThrowIfNullOrEmpty(candidate);
        string rootPrefix = canonicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
        bool result = candidate.StartsWith(rootPrefix, Comparison);
        return result;
    }

    internal static DirectoryPathIdentity Platform { get; } = new(!OperatingSystem.IsWindows());
}
