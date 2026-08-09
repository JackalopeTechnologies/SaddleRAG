// FileSystemProbe.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>The real-disk <see cref="IFileSystemProbe" /> used outside tests.</summary>
public sealed class FileSystemProbe : IFileSystemProbe
{
    public bool FileExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return File.Exists(path);
    }

    public bool DirectoryExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Directory.Exists(path);
    }

    public string? FindOnPath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        string searchPath = Environment.GetEnvironmentVariable(PathVariableName) ?? string.Empty;
        string? result = searchPath
                         .Split(Path.PathSeparator,
                                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Select(directory => Path.Combine(directory, fileName))
                         .FirstOrDefault(File.Exists);
        return result;
    }

    private const string PathVariableName = "PATH";
}
