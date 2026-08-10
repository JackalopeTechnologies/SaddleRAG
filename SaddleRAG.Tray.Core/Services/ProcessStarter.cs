// ProcessStarter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Diagnostics;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Starts a real process and drains its output into a launch log.
///     <para>
///         The draining matters: the launcher redirects stdout and stderr so Docling's own
///         start-up complaints are recoverable by path, and a redirected stream nobody reads
///         eventually fills its pipe buffer and blocks the child.
///     </para>
/// </summary>
public sealed class ProcessStarter : IProcessStarter
{
    public ProcessStarter(string? logPath = null)
    {
        mLogPath = logPath ?? DefaultLogPath;
    }

    private readonly string mLogPath;
    private readonly Lock mLogSync = new();

    /// <summary>Where Docling's own start-up output is captured for the user to read.</summary>
    public static string DefaultLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     LogDirectoryName,
                     LogsFolderName,
                     LogFileName);

    public IDisposable? Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        string? directory = Path.GetDirectoryName(mLogPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        Process? process = Process.Start(startInfo);
        if (process != null)
        {
            process.OutputDataReceived += (_, e) => AppendLine(e.Data);
            process.ErrorDataReceived += (_, e) => AppendLine(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        return process;
    }

    private void AppendLine(string? line)
    {
        if (line != null)
        {
            try
            {
                lock(mLogSync)
                    File.AppendAllText(mLogPath, line + System.Environment.NewLine);
            }
            catch(Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing a log line must never take down the tray; the process itself is
                // unaffected and the drain continues on the next line.
            }
        }
    }

    private const string LogDirectoryName = "SaddleRAG";
    private const string LogsFolderName = "logs";
    private const string LogFileName = "docling-launch.log";
}
