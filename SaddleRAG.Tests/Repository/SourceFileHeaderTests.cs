// SourceFileHeaderTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Tests.Installer;

#endregion

namespace SaddleRAG.Tests.Repository;

/// <summary>
///     Every C#, PowerShell and Razor source file starts with the repository's 4-line MIT header.
///     Build output, the git folder, local scratch and agent folders, the archive, and generated
///     files are not source and are skipped.
/// </summary>
public sealed class SourceFileHeaderTests
{
    [Fact]
    public void EverySourceFileCarriesTheMitHeaderInItsFirstFourLines()
    {
        string? root = InstallerSourceTreeResolver.TryResolveRepositoryRoot();
        if (root == null)
            Assert.Skip(RepositoryMissingSkipReason);
        Assert.NotNull(root);

        List<string> missing = SourceFiles(root).Where(path => !HasHeader(path))
                                                .Select(path => Path.GetRelativePath(root, path))
                                                .Order(StringComparer.Ordinal)
                                                .ToList();

        Assert.True(missing.Count == 0,
                    $"{missing.Count} source file(s) lack \"{LicenseMarker}\" in their first {HeaderLineCount} lines:"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, missing));
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                 .Where(path => smExtensions.Contains(Path.GetExtension(path)))
                 .Where(path => !IsGenerated(path))
                 .Where(path => !IsInExcludedFolder(root, path));

    private static bool IsInExcludedFolder(string root, string path)
    {
        string[] folders = Path.GetRelativePath(root, path)
                               .Split(smSeparators, StringSplitOptions.RemoveEmptyEntries);
        bool result = folders.Take(folders.Length - 1).Any(folder => smExcludedFolders.Contains(folder));
        return result;
    }

    private static bool IsGenerated(string path)
    {
        string name = Path.GetFileName(path);
        bool result = smGeneratedSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static bool HasHeader(string path)
    {
        bool result = File.ReadLines(path)
                          .Take(HeaderLineCount)
                          .Any(line => line.Contains(LicenseMarker, StringComparison.Ordinal));
        return result;
    }

    private const int HeaderLineCount = 4;
    private const string LicenseMarker = "SPDX-License-Identifier: MIT";
    private const string RepositoryMissingSkipReason = "The repository source tree is not available.";

    private static readonly HashSet<string> smExtensions = new([".cs", ".ps1", ".razor"], StringComparer.OrdinalIgnoreCase);

    // Not source: build output, git internals, untracked scratch and agent folders, and the archive
    // of deprecated material kept for history.
    private static readonly HashSet<string> smExcludedFolders =
        new(["bin", "obj", ".git", "Scratch", "archive", ".claude", ".superpowers", ".vs", "TestResults", "node_modules"],
            StringComparer.OrdinalIgnoreCase);

    private static readonly string[] smGeneratedSuffixes = [".g.cs", ".g.i.cs", ".designer.cs"];

    private static readonly char[] smSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
}
