// RunSentinel.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json;

#endregion

namespace SaddleRAG.Mcp;

/// <summary>
///     Crash black box (issue #139). Writes a running-marker file at startup
///     and deletes it on graceful shutdown; a marker already present at the
///     next startup means the previous run died dirty (native access
///     violation, kill, power loss) and its recorded identity is returned so
///     startup can log an Error with what was running. Also owns the
///     last-crash marker written by the unhandled-exception hook so managed
///     crash details survive process death. Both files live in the SaddleRAG
///     data root next to the logs, where the crash triage tooling can read
///     them.
/// </summary>
public sealed class RunSentinel
{
    /// <summary>
    ///     Binds the sentinel to <paramref name="dataDirectory" />; the
    ///     directory is created on demand by the write operations.
    /// </summary>
    /// <param name="dataDirectory">Directory holding the marker files.</param>
    public RunSentinel(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);

        mRunningMarkerPath = Path.Combine(dataDirectory, RunningMarkerFileName);
        mLastCrashPath = Path.Combine(dataDirectory, LastCrashFileName);
        mDataDirectory = dataDirectory;
    }

    /// <summary>
    ///     Identity of a previous run recovered from a leftover running
    ///     marker. <see cref="Pid" /> is <see cref="UnknownPid" /> when the
    ///     marker existed but could not be parsed.
    /// </summary>
    public sealed record PriorRunInfo(int Pid, string Version, DateTime StartedUtc);

    private readonly string mDataDirectory;
    private readonly string mRunningMarkerPath;
    private readonly string mLastCrashPath;

    /// <summary>
    ///     Records the current run in the running marker. Returns the
    ///     identity of the previous run when its marker was still present
    ///     (dirty shutdown), or null when the last shutdown was clean.
    /// </summary>
    /// <param name="pid">Current process id.</param>
    /// <param name="version">Product version of the current run.</param>
    /// <param name="startedUtc">Start timestamp of the current run.</param>
    public PriorRunInfo? MarkStarted(int pid, string version, DateTime startedUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);

        PriorRunInfo? priorRun = null;

        if (File.Exists(mRunningMarkerPath))
            priorRun = ReadPriorRun();

        Directory.CreateDirectory(mDataDirectory);
        string json = JsonSerializer.Serialize(new PriorRunInfo(pid, version, startedUtc));
        File.WriteAllText(mRunningMarkerPath, json);

        return priorRun;
    }

    /// <summary>
    ///     Deletes the running marker; call on graceful shutdown
    ///     (ApplicationStopped). Safe to call when no marker exists.
    /// </summary>
    public void MarkStoppedCleanly()
    {
        if (File.Exists(mRunningMarkerPath))
            File.Delete(mRunningMarkerPath);
    }

    /// <summary>
    ///     Persists the details of a terminal unhandled exception so they
    ///     survive process death. Best-effort: called from a crashing
    ///     process, so IO failures are swallowed — there is nowhere left to
    ///     report them.
    /// </summary>
    /// <param name="exception">The unhandled exception.</param>
    /// <param name="crashedUtc">Timestamp of the crash.</param>
    public void WriteCrashMarker(Exception exception, DateTime crashedUtc)
    {
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            Directory.CreateDirectory(mDataDirectory);
            string text = string.Format(CrashMarkerFormat, crashedUtc.ToString(TimestampFormat), exception);
            File.WriteAllText(mLastCrashPath, text);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    ///     Returns the last recorded crash text, or null when no managed
    ///     crash has been recorded.
    /// </summary>
    public string? TryReadLastCrash()
    {
        string? result = null;

        if (File.Exists(mLastCrashPath))
            result = File.ReadAllText(mLastCrashPath);

        return result;
    }

    private PriorRunInfo ReadPriorRun()
    {
        PriorRunInfo result;

        try
        {
            string json = File.ReadAllText(mRunningMarkerPath);
            result = JsonSerializer.Deserialize<PriorRunInfo>(json) ?? BuildUnknownPriorRun();
        }
        catch(Exception ex) when(ex is JsonException or IOException or UnauthorizedAccessException)
        {
            result = BuildUnknownPriorRun();
        }

        return result;
    }

    private static PriorRunInfo BuildUnknownPriorRun() =>
        new(UnknownPid, UnknownVersion, DateTime.MinValue);

    /// <summary>Pid reported when a leftover marker could not be parsed.</summary>
    public const int UnknownPid = -1;

    /// <summary>File name of the running marker inside the data directory.</summary>
    public const string RunningMarkerFileName = "running.marker";

    /// <summary>File name of the last-crash marker inside the data directory.</summary>
    public const string LastCrashFileName = "last-crash.txt";

    private const string UnknownVersion = "unknown";
    private const string TimestampFormat = "O";
    private const string CrashMarkerFormat = "Crashed at {0} (UTC)\r\n{1}";
}
