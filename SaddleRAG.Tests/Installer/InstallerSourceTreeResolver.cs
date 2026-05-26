// InstallerSourceTreeResolver.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Tests.Installer;

/// <summary>
///     Walks up from the test assembly's binary directory to the
///     repository root (identified by the presence of
///     <c>SaddleRAG.slnx</c>) and resolves paths to source files under
///     <c>SaddleRAG.Installer/</c>. Used by the installer tests that need
///     to read the live WiX / JScript / PowerShell sources at runtime
///     (e.g., to pin a string-literal against the live file, to mirror an
///     algorithm against its test oracle, or to drive the production
///     <c>.ps1</c> via <c>Process.Start</c>).
///     <para>
///         Returns <c>null</c> when run from a detached <c>bin</c>
///         directory with no parent matching the repo root. Callers
///         should treat that as a "skip this test" signal rather than
///         a hard failure — a missing repo root means the test
///         environment is unusual, not that the production code is
///         broken.
///     </para>
/// </summary>
internal static class InstallerSourceTreeResolver
{
    /// <summary>
    ///     Returns the absolute path to <paramref name="relativeFileName" />
    ///     under <c>SaddleRAG.Installer/</c>, or <c>null</c> if the
    ///     repository root can't be located from
    ///     <see cref="AppContext.BaseDirectory" /> or if the requested
    ///     file isn't present.
    /// </summary>
    internal static string? TryResolveInstallerFile(string relativeFileName)
    {
        string? root = TryResolveRepositoryRoot();
        string? result = null;
        if (root != null)
        {
            string candidate = Path.Combine(root, InstallerFolderName, relativeFileName);
            if (File.Exists(candidate))
                result = candidate;
        }
        return result;
    }

    /// <summary>
    ///     Returns the absolute path to the repository root, or
    ///     <c>null</c> if not locatable from
    ///     <see cref="AppContext.BaseDirectory" />.
    /// </summary>
    internal static string? TryResolveRepositoryRoot()
    {
        string testBinDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        DirectoryInfo? dir = new DirectoryInfo(testBinDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, RepositoryRootMarker)))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private const string RepositoryRootMarker = "SaddleRAG.slnx";
    private const string InstallerFolderName = "SaddleRAG.Installer";
}
