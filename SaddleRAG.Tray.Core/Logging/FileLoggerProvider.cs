// FileLoggerProvider.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Tray.Core.Logging;

/// <summary>
///     Minimal append-to-file <see cref="ILoggerProvider" /> for the tray, which runs un-elevated
///     and has no host to supply a logging stack. Writes one line per entry under a path the
///     caller chooses (the tray uses %LocalAppData%\SaddleRAG\tray.log).
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public FileLoggerProvider(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        mFilePath = filePath;
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    private readonly string mFilePath;
    private readonly object mGate = new object();

    public ILogger CreateLogger(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);
        return new FileLogger(categoryName, this);
    }

    public void Dispose()
    {
    }

    internal void Append(string line)
    {
        lock(mGate)
        {
            File.AppendAllText(mFilePath, line);
        }
    }
}
