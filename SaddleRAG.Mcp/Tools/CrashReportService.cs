// CrashReportService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     Builds the one-call crash post-mortem (issue #140): Windows event-log
///     records (via <see cref="ICrashEventReader" />), WER AppCrash report
///     folders, captured crash dumps (issue #136), the managed last-crash
///     marker (issue #139), and the tail of the current service log
///     (issue #138). Replaces manually correlating four separate Windows
///     locations after a service death.
/// </summary>
public sealed class CrashReportService
{
    /// <summary>
    ///     Filesystem locations the report is built from, registered in DI at
    ///     startup. Sealed class rather than a positional record for the same
    ///     MCP parameter-marshalling reason as
    ///     <see cref="DiagnosticTools.LogConfig" />.
    /// </summary>
    public sealed class Options
    {
        public Options(string dumpFolder, IReadOnlyList<string> werReportRoots, string logDirectory)
        {
            ArgumentException.ThrowIfNullOrEmpty(dumpFolder);
            ArgumentNullException.ThrowIfNull(werReportRoots);
            ArgumentException.ThrowIfNullOrEmpty(logDirectory);

            DumpFolder = dumpFolder;
            WerReportRoots = werReportRoots;
            LogDirectory = logDirectory;
        }

        public string DumpFolder { get; }
        public IReadOnlyList<string> WerReportRoots { get; }
        public string LogDirectory { get; }
    }

    public CrashReportService(Options options, RunSentinel sentinel, ICrashEventReader eventReader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sentinel);
        ArgumentNullException.ThrowIfNull(eventReader);

        mOptions = options;
        mSentinel = sentinel;
        mEventReader = eventReader;
    }

    private readonly Options mOptions;
    private readonly RunSentinel mSentinel;
    private readonly ICrashEventReader mEventReader;

    /// <summary>Assembles the current crash evidence into one report.</summary>
    public CrashReport Build()
    {
        return new CrashReport(mEventReader.ReadLastRuntimeCrash(),
                               mEventReader.ReadLastFault(),
                               mEventReader.ReadLastServiceTermination(),
                               CollectWerReports(),
                               CollectCrashDumps(),
                               mSentinel.TryReadLastCrash(),
                               CollectLogTail()
                              );
    }

    private IReadOnlyList<CrashDumpInfo> CollectCrashDumps()
    {
        IReadOnlyList<CrashDumpInfo> result = [];

        if (Directory.Exists(mOptions.DumpFolder))
        {
            result = new DirectoryInfo(mOptions.DumpFolder)
                     .EnumerateFiles(DumpFilePattern)
                     .OrderByDescending(f => f.LastWriteTimeUtc)
                     .Select(f => new CrashDumpInfo(f.Name, f.Length, f.LastWriteTimeUtc))
                     .ToList();
        }

        return result;
    }

    private IReadOnlyList<WerReportInfo> CollectWerReports()
    {
        return mOptions.WerReportRoots
                       .Where(Directory.Exists)
                       .SelectMany(root => new DirectoryInfo(root).EnumerateDirectories(WerReportPattern))
                       .OrderByDescending(d => d.LastWriteTimeUtc)
                       .Select(d => new WerReportInfo(d.Name, d.LastWriteTimeUtc))
                       .ToList();
    }

    private IReadOnlyList<string> CollectLogTail()
    {
        IReadOnlyList<string> result = [];

        FileInfo? latest = null;
        if (Directory.Exists(mOptions.LogDirectory))
        {
            latest = new DirectoryInfo(mOptions.LogDirectory)
                     .EnumerateFiles(LogFilePattern)
                     .OrderByDescending(f => f.LastWriteTimeUtc)
                     .FirstOrDefault();
        }

        if (latest != null)
        {
            try
            {
                result = ReadAllLinesShared(latest.FullName).TakeLast(LogTailLineCount).ToList();
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {
                result = [string.Format(LogReadFailureFormat, latest.Name, ex.Message)];
            }
        }

        return result;
    }

    private static List<string> ReadAllLinesShared(string path)
    {
        var lines = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        string? line = reader.ReadLine();
        while (line != null)
        {
            lines.Add(line);
            line = reader.ReadLine();
        }

        return lines;
    }

    /// <summary>Number of trailing log lines included in the report.</summary>
    public const int LogTailLineCount = 20;

    private const string DumpFilePattern = "*.dmp";
    private const string WerReportPattern = "AppCrash_SaddleRAG.Mcp*";
    private const string LogFilePattern = "saddlerag-*.log";
    private const string LogReadFailureFormat = "(failed to read log file '{0}': {1})";
}
