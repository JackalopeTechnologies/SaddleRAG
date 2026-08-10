// IFileSystemProbe.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     The disk lookups tool detection needs, behind a seam so detection can be tested
///     without depending on what happens to be installed on the build machine.
/// </summary>
public interface IFileSystemProbe
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>Resolves <paramref name="fileName" /> against PATH, or null when absent.</summary>
    string? FindOnPath(string fileName);
}
