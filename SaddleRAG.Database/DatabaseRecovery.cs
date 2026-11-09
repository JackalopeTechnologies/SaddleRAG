// DatabaseRecovery.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace SaddleRAG.Database;

/// <summary>
///     Restarts a stopped local MongoDB Windows service when a request needs the
///     database, so a MongoDB that stopped (a crash, an update, a reboot that left it
///     stopped) comes back without anyone starting it by hand.
///     <para>
///         It acts only when a database on this computer is already known to be down;
///         otherwise it returns at once without a lock or any I/O. Every caller that
///         arrives during an outage joins the same single attempt. The attempt waits up
///         to <see cref="DatabaseRecoveryTimings.RestartWait" /> for the database to
///         answer; when it fails, callers fail at once with the same report for
///         <see cref="DatabaseRecoveryTimings.FailurePause" /> instead of each paying the
///         full wait again.
///     </para>
///     <para>
///         It never stops a service. A service that is running or starting but not
///         answering may be busy recovering its data files after a crash; stopping it
///         could interrupt that recovery, and the database may be shared with other
///         programs. Such a service is waited on and reported, never stopped.
///     </para>
///     <para>
///         The attempt is detached from the callers: it starts with the execution
///         context suppressed, so no caller's fail-fast scope (which would make the
///         recovery ping give up during the outage it is trying to end) or
///         cancellation reaches it. A caller that gives up leaves; the attempt carries
///         on for the callers still waiting and for the next request.
///     </para>
/// </summary>
public sealed class DatabaseRecovery
{
    public DatabaseRecovery(Func<IReadOnlyList<IRecoverableDatabase>> openedDatabases,
                            IMongoServiceControl serviceControl,
                            IOptions<SaddleRagDbSettings> settings,
                            TimeProvider timeProvider,
                            ILogger<DatabaseRecovery> logger,
                            DatabaseRecoveryTimings? timings = null)
    {
        ArgumentNullException.ThrowIfNull(openedDatabases);
        ArgumentNullException.ThrowIfNull(serviceControl);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        mOpenedDatabases = openedDatabases;
        mServiceControl = serviceControl;
        mAutoRestart = settings.Value.AutoRestart;
        mTimeProvider = timeProvider;
        mLogger = logger;
        mTimings = timings ?? DatabaseRecoveryTimings.Default;
    }

    private readonly MongoAutoRestartSettings mAutoRestart;
    private readonly Lock mLock = new Lock();
    private readonly ILogger<DatabaseRecovery> mLogger;
    private readonly Func<IReadOnlyList<IRecoverableDatabase>> mOpenedDatabases;
    private readonly IMongoServiceControl mServiceControl;
    private readonly TimeProvider mTimeProvider;
    private readonly DatabaseRecoveryTimings mTimings;
    private Task? mCurrentAttempt;
    private string mLastFailureReport = string.Empty;
    private long? mLastFailureTimestamp;

    /// <summary>
    ///     Returns at once unless a local database is known to be down; then joins (or
    ///     starts) the single restart attempt and waits for it. Cancelling
    ///     <paramref name="ct" /> stops this caller's wait, not the attempt.
    /// </summary>
    /// <exception cref="DatabaseUnavailableException">
    ///     The restart failed, or a recent failure's pause is still in effect;
    ///     <see cref="DatabaseUnavailableException.RestartReport" /> says what happened.
    /// </exception>
    public Task EnsureAvailableAsync(CancellationToken ct)
    {
        Task result = Task.CompletedTask;
        if (mAutoRestart.Enabled)
        {
            var down = mOpenedDatabases().Where(database => database.IsLocal && database.IsKnownDown).ToList();
            if (down.Count > 0)
                result = JoinAttempt(down).WaitAsync(ct);
        }

        return result;
    }

    private Task JoinAttempt(IReadOnlyList<IRecoverableDatabase> down)
    {
        string endpoint = DescribeEndpoints(down);
        Task attempt;
        lock(mLock)
        {
            if (IsInFailurePause())
                attempt = Task.FromException(new DatabaseUnavailableException(endpoint, mLastFailureReport, null));
            else
                attempt = mCurrentAttempt ??= StartAttempt(down, endpoint);
        }

        return attempt;
    }

    private bool IsInFailurePause() =>
        mLastFailureTimestamp.HasValue &&
        mTimeProvider.GetElapsedTime(mLastFailureTimestamp.Value) < mTimings.FailurePause;

    private Task StartAttempt(IReadOnlyList<IRecoverableDatabase> down, string endpoint)
    {
        Task attempt;
        using (ExecutionContext.SuppressFlow())
        {
            attempt = Task.Run(() => RunAttemptAsync(down, endpoint), CancellationToken.None);
        }

        return attempt;
    }

    private async Task RunAttemptAsync(IReadOnlyList<IRecoverableDatabase> down, string endpoint)
    {
        string serviceName = mAutoRestart.ServiceName;
        var startedAt = mTimeProvider.GetLocalNow();
        long startTimestamp = mTimeProvider.GetTimestamp();
        string? report = null;
        Exception? error = null;
        bool finished = false;
        try
        {
            (string? reason, error, bool startedService) = await RecoverAsync(down, serviceName);
            if (reason == null)
                LogRecovered(endpoint, serviceName, startedService, startTimestamp);
            else
            {
                report = string.Format(CultureInfo.InvariantCulture,
                                       ReportFormat,
                                       serviceName,
                                       startedAt.ToString(ReportTimeFormat, CultureInfo.InvariantCulture),
                                       reason
                                      );
                mLogger.LogError(error, "MongoDB at {Endpoint} is still unreachable. {RestartReport}", endpoint, report);
            }

            finished = true;
        }
        finally
        {
            lock(mLock)
            {
                mCurrentAttempt = null;
                if (finished)
                    RecordOutcome(report);
            }
        }

        if (report != null)
            throw new DatabaseUnavailableException(endpoint, report, error);
    }

    private void RecordOutcome(string? failureReport)
    {
        mLastFailureTimestamp = failureReport == null ? null : mTimeProvider.GetTimestamp();
        mLastFailureReport = failureReport ?? string.Empty;
    }

    private void LogRecovered(string endpoint, string serviceName, bool startedService, long startTimestamp)
    {
        double seconds = Math.Round(mTimeProvider.GetElapsedTime(startTimestamp).TotalSeconds, SecondsDecimals);
        string action = startedService ? StartedAction : WaitedAction;
        mLogger.LogWarning("MongoDB at {Endpoint} is answering again after {Seconds} s. SaddleRAG {Action} the '{ServiceName}' service.",
                           endpoint,
                           seconds,
                           action,
                           serviceName
                          );
    }

    private async Task<(string? Reason, Exception? Error, bool StartedService)> RecoverAsync(
        IReadOnlyList<IRecoverableDatabase> down,
        string serviceName)
    {
        using var deadline = new CancellationTokenSource(mTimings.RestartWait, mTimeProvider);
        (string? reason, Exception? error, bool startedService) = await BringServiceUpAsync(serviceName, deadline.Token);
        if (reason == null && !await WaitForAnswersAsync(down, deadline.Token))
        {
            string seconds = mTimings.RestartWait.TotalSeconds.ToString(SecondsFormat, CultureInfo.InvariantCulture);
            reason = string.Format(CultureInfo.InvariantCulture,
                                   startedService ? DidNotAnswerReason : RunningNotAnsweringReason,
                                   seconds
                                  );
        }

        return (reason, error, startedService);
    }

    /// <summary>
    ///     Reads the service's state and starts it only when it is stopped. A stopping
    ///     service is polled until it has stopped (or the deadline passes, after which it
    ///     is treated as stopped). Running, starting and other states are left alone.
    /// </summary>
    private async Task<(string? Reason, Exception? Error, bool StartedService)> BringServiceUpAsync(
        string serviceName,
        CancellationToken deadline)
    {
        (var state, string? reason, var error) = ReadState(serviceName);
        bool startedService = false;
        if (state == MongoServiceState.StopPending)
            (state, reason, error) = await WaitWhileStoppingAsync(serviceName, deadline);

        switch (state)
        {
            case MongoServiceState.NotInstalled:
                reason = string.Format(CultureInfo.InvariantCulture, NotInstalledReason, serviceName);
                break;
            case MongoServiceState.Unsupported:
                reason = UnsupportedReason;
                break;
            case MongoServiceState.Stopped:
                startedService = true;
                (reason, error) = TryStart(serviceName);
                break;
            default:
                // Running, starting, other states, or a failed read (whose reason is
                // already set): never start a second time and never stop.
                break;
        }

        return (reason, error, startedService);
    }

    private async Task<(MongoServiceState State, string? Reason, Exception? Error)> WaitWhileStoppingAsync(
        string serviceName,
        CancellationToken deadline)
    {
        (MongoServiceState State, string? Reason, Exception? Error) current =
            (MongoServiceState.StopPending, null, null);
        bool deadlinePassed = false;
        while (current.State == MongoServiceState.StopPending && !deadlinePassed)
        {
            deadlinePassed = !await DelayAsync(mTimings.PingInterval, deadline);
            if (!deadlinePassed)
                current = ReadState(serviceName);
        }

        if (current.State == MongoServiceState.StopPending)
            current.State = MongoServiceState.Stopped;

        return current;
    }

    /// <summary>
    ///     Any failure to read the state becomes the report: the attempt must always end
    ///     in a <see cref="DatabaseUnavailableException" />, never another exception.
    ///     A failed read reports <see cref="MongoServiceState.Other" />, so nothing is started.
    /// </summary>
    private (MongoServiceState State, string? Reason, Exception? Error) ReadState(string serviceName)
    {
        (MongoServiceState State, string? Reason, Exception? Error) result;
        try
        {
            result = (mServiceControl.GetState(serviceName), null, null);
        }
        catch(Exception ex)
        {
            result = (MongoServiceState.Other, DescribeFailure(ex, StateUnreadableReason), ex);
        }

        return result;
    }

    /// <summary>Any failure to start becomes the report, for the same reason as <see cref="ReadState" />.</summary>
    private (string? Reason, Exception? Error) TryStart(string serviceName)
    {
        (string? Reason, Exception? Error) result = (null, null);
        try
        {
            mServiceControl.Start(serviceName);
        }
        catch(Exception ex)
        {
            result = (DescribeFailure(ex, StartRefusedReason), ex);
        }

        return result;
    }

    private static string DescribeFailure(Exception ex, string reasonFormat) =>
        IsAccessDenied(ex) || IsAccessDenied(ex.InnerException)
            ? NoPermissionReason
            : string.Format(CultureInfo.InvariantCulture, reasonFormat, ex.Message.TrimEnd(SentenceEnd));

    private static bool IsAccessDenied(Exception? ex) =>
        ex is Win32Exception { NativeErrorCode: AccessDeniedError };

    /// <summary>
    ///     Pings each database still silent, one round at a time, until all answer or the
    ///     deadline passes.
    /// </summary>
    private async Task<bool> WaitForAnswersAsync(IReadOnlyList<IRecoverableDatabase> down, CancellationToken deadline)
    {
        var waiting = down.ToList();
        bool deadlinePassed = false;
        while (waiting.Count > 0 && !deadlinePassed)
        {
            var silent = new List<IRecoverableDatabase>();
            foreach (var database in waiting)
            {
                if (!await PingAsync(database, deadline))
                    silent.Add(database);
            }

            waiting = silent;
            if (waiting.Count > 0)
                deadlinePassed = !await DelayAsync(mTimings.PingInterval, deadline);
        }

        return waiting.Count == 0;
    }

    /// <summary>
    ///     A failed ping is the expected answer while MongoDB starts, so it is neither
    ///     thrown nor logged; the attempt's own outcome is logged once.
    /// </summary>
    private async Task<bool> PingAsync(IRecoverableDatabase database, CancellationToken deadline)
    {
        using var timeout = new CancellationTokenSource(mTimings.PingTimeout, mTimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline, timeout.Token);
        var ping = database.CheckHealthAsync(linked.Token);
        await ping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return ping.IsCompletedSuccessfully;
    }

    /// <summary>True when the full delay passed; false when the deadline cut it short.</summary>
    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken deadline)
    {
        await Task.Delay(delay, mTimeProvider, deadline).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return !deadline.IsCancellationRequested;
    }

    private static string DescribeEndpoints(IEnumerable<IRecoverableDatabase> databases) =>
        string.Join(EndpointSeparator,
                    databases.Select(database => database.Endpoint)
                             .Distinct(StringComparer.Ordinal)
                             .Order(StringComparer.Ordinal)
                   );

    private const int AccessDeniedError = 5;
    private const int SecondsDecimals = 1;
    private const char SentenceEnd = '.';
    private const string EndpointSeparator = ", ";
    private const string SecondsFormat = "0.#";
    private const string ReportTimeFormat = "h:mm tt";
    private const string ReportFormat = "SaddleRAG tried to restart the '{0}' service at {1} and {2}.";
    private const string NotInstalledReason = "the '{0}' service is not installed on this computer";
    private const string UnsupportedReason = "this computer cannot control Windows services";
    private const string NoPermissionReason = "SaddleRAG does not have permission to start it";
    private const string StartRefusedReason = "Windows refused to start it: {0}";
    private const string StateUnreadableReason = "SaddleRAG could not read the service's state: {0}";
    private const string DidNotAnswerReason = "it did not answer within {0} seconds";

    private const string RunningNotAnsweringReason =
        "the service is running but did not answer within {0} seconds; SaddleRAG does not stop a running service";

    private const string StartedAction = "started";
    private const string WaitedAction = "waited for the already starting";
}
