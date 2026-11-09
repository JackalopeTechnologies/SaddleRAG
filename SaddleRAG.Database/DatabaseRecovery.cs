// DatabaseRecovery.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

#endregion

namespace SaddleRAG.Database;

/// <summary>
///     Restarts a stopped local MongoDB Windows service when a request needs the
///     database, so a MongoDB that stopped (a crash, an update, a reboot that left it
///     stopped) comes back without anyone starting it by hand.
///     <para>
///         It looks only at the databases requests use (in SaddleRAG, the active
///         profile's), and acts only when one of them is on this computer and already
///         known to be down; otherwise it returns at once without a lock or any I/O.
///         Every caller that arrives during an outage joins the same single attempt.
///         The attempt waits up to <see cref="DatabaseRecoveryTimings.RestartWait" /> for
///         the database to answer; after a failure no new attempt starts for
///         <see cref="DatabaseRecoveryTimings.FailurePause" />.
///     </para>
///     <para>
///         It never fails a request. Whether or not the attempt worked, the caller
///         carries on; a request that then touches the database fails the way it would
///         have without this class, and <see cref="WithRestartReport" /> adds the latest
///         attempt's report to that outage message. Requests that never touch the
///         database (server logs, crash reports) keep working during the outage they
///         help diagnose.
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
    /// <param name="databases">
    ///     The databases requests use. Called on every request, so it must open nothing
    ///     new and perform no I/O.
    /// </param>
    /// <param name="serviceControl">Reads and starts the Windows service.</param>
    /// <param name="settings">Whether restart is on, and the service name.</param>
    /// <param name="timeProvider">The clock for the waits and the failure pause.</param>
    /// <param name="logger">Receives one Warning per restart that worked and one Error per attempt that failed.</param>
    /// <param name="timings">The fixed waits; only tests pass shorter ones.</param>
    /// <param name="stopping">
    ///     Cancelled when the host begins shutting down; from then on no new attempt
    ///     starts, so SaddleRAG does not start MongoDB on its way out.
    /// </param>
    public DatabaseRecovery(Func<IReadOnlyList<IRecoverableDatabase>> databases,
                            IMongoServiceControl serviceControl,
                            IOptions<SaddleRagDbSettings> settings,
                            TimeProvider timeProvider,
                            ILogger<DatabaseRecovery> logger,
                            DatabaseRecoveryTimings? timings = null,
                            CancellationToken stopping = default)
    {
        ArgumentNullException.ThrowIfNull(databases);
        ArgumentNullException.ThrowIfNull(serviceControl);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        mDatabases = databases;
        mServiceControl = serviceControl;
        mAutoRestart = settings.Value.AutoRestart;
        mTimeProvider = timeProvider;
        mLogger = logger;
        mTimings = timings ?? DatabaseRecoveryTimings.Default;
        mStopping = stopping;
    }

    private readonly MongoAutoRestartSettings mAutoRestart;
    private readonly Func<IReadOnlyList<IRecoverableDatabase>> mDatabases;
    private readonly Lock mLock = new Lock();
    private readonly ILogger<DatabaseRecovery> mLogger;
    private readonly IMongoServiceControl mServiceControl;
    private readonly CancellationToken mStopping;
    private readonly TimeProvider mTimeProvider;
    private readonly DatabaseRecoveryTimings mTimings;
    private Task? mCurrentAttempt;
    private string mLastFailureReport = string.Empty;
    private long? mLastFailureTimestamp;

    /// <summary>
    ///     Returns at once unless a local database is known to be down; then joins (or
    ///     starts) the single restart attempt and waits for it. During the failure pause
    ///     it returns at once without a new attempt. It completes normally whether or not
    ///     the database came back. Once the host is shutting down it returns at once.
    ///     Cancelling <paramref name="ct" /> stops this caller's wait (and throws
    ///     <see cref="OperationCanceledException" />), not the attempt.
    /// </summary>
    public Task EnsureAvailableAsync(CancellationToken ct)
    {
        Task result = Task.CompletedTask;
        if (mAutoRestart.Enabled && !mStopping.IsCancellationRequested)
        {
            var down = FindLocalDown();
            if (down.Count > 0)
                result = JoinAttempt(down).WaitAsync(ct);
        }

        return result;
    }

    /// <summary>
    ///     Adds the latest failed attempt's report to an outage a request ran into, so the
    ///     caller learns what SaddleRAG tried. The report is added only while a local
    ///     database is still known to be down after that failed attempt; otherwise, or
    ///     when the outage already carries a report, <paramref name="unavailable" /> is
    ///     returned unchanged.
    /// </summary>
    public DatabaseUnavailableException WithRestartReport(DatabaseUnavailableException unavailable)
    {
        ArgumentNullException.ThrowIfNull(unavailable);

        string? report = unavailable.RestartReport == null ? GetCurrentFailureReport() : null;
        var result = report == null
                         ? unavailable
                         : new DatabaseUnavailableException(unavailable.Endpoint, report, unavailable.InnerException);
        return result;
    }

    private string? GetCurrentFailureReport()
    {
        string report;
        lock(mLock)
        {
            report = mLastFailureReport;
        }

        string? result = report.Length > 0 && FindLocalDown().Count > 0 ? report : null;
        return result;
    }

    private List<IRecoverableDatabase> FindLocalDown() =>
        mDatabases().Where(database => database.IsLocal && database.IsKnownDown).ToList();

    private Task JoinAttempt(IReadOnlyList<IRecoverableDatabase> down)
    {
        Task attempt;
        lock(mLock)
        {
            if (IsInFailurePause())
                attempt = Task.CompletedTask;
            else
                attempt = mCurrentAttempt ??= StartAttempt(down);
        }

        return attempt;
    }

    private bool IsInFailurePause() =>
        mLastFailureTimestamp.HasValue &&
        mTimeProvider.GetElapsedTime(mLastFailureTimestamp.Value) < mTimings.FailurePause;

    private Task StartAttempt(IReadOnlyList<IRecoverableDatabase> down)
    {
        Task attempt;
        using (ExecutionContext.SuppressFlow())
        {
            attempt = Task.Run(() => RunAttemptAsync(down), CancellationToken.None);
        }

        return attempt;
    }

    /// <summary>
    ///     Runs one attempt, logs how it ended, and records its report for
    ///     <see cref="WithRestartReport" />. It never throws: callers are never failed by
    ///     the attempt, and a failure nobody is still waiting for is not left as an
    ///     unobserved task exception (which the server would log as a second Error).
    /// </summary>
    private async Task RunAttemptAsync(IReadOnlyList<IRecoverableDatabase> down)
    {
        string endpoint = DescribeEndpoints(down);
        string serviceName = mAutoRestart.ServiceName;
        var startedAt = mTimeProvider.GetLocalNow();
        long startTimestamp = mTimeProvider.GetTimestamp();
        (string? Reason, Exception? Error, RecoveredServiceState Observed) attempt;
        try
        {
            attempt = await RecoverAsync(down, serviceName);
        }
        catch(Exception ex)
        {
            // Every expected failure already becomes a reason; anything else is still
            // reported (and logged as this attempt's Error below) rather than thrown.
            attempt = (DescribeFailure(ex, UnexpectedFailureReason), ex, RecoveredServiceState.WasRunning);
        }

        string? report = attempt.Reason == null
                             ? null
                             : string.Format(CultureInfo.InvariantCulture,
                                             ReportFormat,
                                             serviceName,
                                             startedAt.ToString(ReportTimeFormat, CultureInfo.InvariantCulture),
                                             attempt.Reason
                                            );

        // Recorded before logging, so nothing that happens while logging can leave a
        // finished attempt looking unfinished.
        lock(mLock)
        {
            mCurrentAttempt = null;
            mLastFailureTimestamp = report == null ? null : mTimeProvider.GetTimestamp();
            mLastFailureReport = report ?? string.Empty;
        }

        if (report == null)
            LogRecovered(endpoint, serviceName, attempt.Observed, startTimestamp);
        else
        {
            mLogger.LogError(attempt.Error,
                             "MongoDB at {Endpoint} is still unreachable. {RestartReport}",
                             endpoint,
                             report
                            );
        }
    }

    private void LogRecovered(string endpoint,
                              string serviceName,
                              RecoveredServiceState observed,
                              long startTimestamp)
    {
        double seconds = Math.Round(mTimeProvider.GetElapsedTime(startTimestamp).TotalSeconds, SecondsDecimals);
        string actionFormat = observed switch
            {
                RecoveredServiceState.StartedBySaddleRag => StartedAction,
                RecoveredServiceState.WasStarting => WasStartingAction,
                var _ => WasRunningAction
            };
        string action = string.Format(CultureInfo.InvariantCulture, actionFormat, serviceName);
        mLogger.LogWarning("MongoDB at {Endpoint} is answering again after {Seconds} s. {Action}",
                           endpoint,
                           seconds,
                           action
                          );
    }

    private async Task<(string? Reason, Exception? Error, RecoveredServiceState Observed)> RecoverAsync(
        IReadOnlyList<IRecoverableDatabase> down,
        string serviceName)
    {
        using var deadline = new CancellationTokenSource(mTimings.RestartWait, mTimeProvider);
        (string? reason, Exception? error, var observed) = await BringServiceUpAsync(serviceName, deadline.Token);
        if (reason == null)
            (reason, error) = await WaitForAnswersAsync(down, observed, deadline.Token);

        return (reason, error, observed);
    }

    /// <summary>
    ///     Reads the service's state and starts it only when it is stopped. A stopping
    ///     service is polled until it has stopped (or the deadline passes, after which it
    ///     is treated as stopped). Running, starting and other states are left alone.
    /// </summary>
    private async Task<(string? Reason, Exception? Error, RecoveredServiceState Observed)> BringServiceUpAsync(
        string serviceName,
        CancellationToken deadline)
    {
        (var state, string? reason, var error) = ReadState(serviceName);
        var observed = RecoveredServiceState.WasRunning;
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
                (reason, error, observed) = TryStart(serviceName);
                break;
            case MongoServiceState.StartPending:
                observed = RecoveredServiceState.WasStarting;
                break;
            default:
                // Running, other states, or a failed read (whose reason is already set):
                // never start a second time and never stop.
                break;
        }

        return (reason, error, observed);
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
    ///     in its report, never an exception.
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

    /// <summary>
    ///     Any failure to start becomes the report, for the same reason as
    ///     <see cref="ReadState" />. Windows refusing because the service is already
    ///     running means something else started it between the state read and the start
    ///     (another program, or Windows itself); that is not a failure, so the attempt
    ///     carries on as for a service that was already starting.
    /// </summary>
    private (string? Reason, Exception? Error, RecoveredServiceState Observed) TryStart(string serviceName)
    {
        (string? Reason, Exception? Error, RecoveredServiceState Observed) result =
            (null, null, RecoveredServiceState.StartedBySaddleRag);
        try
        {
            mServiceControl.Start(serviceName);
        }
        catch(Exception ex) when(IsAlreadyRunning(ex) || IsAlreadyRunning(ex.InnerException))
        {
            result = (null, null, RecoveredServiceState.WasStarting);
        }
        catch(Exception ex)
        {
            result = (DescribeFailure(ex, StartRefusedReason), ex, RecoveredServiceState.StartedBySaddleRag);
        }

        return result;
    }

    /// <summary>
    ///     Windows' own reason (for example "the service is disabled") travels as the inner
    ///     <see cref="Win32Exception" />; the outer message only says that the service could
    ///     not be started, so the inner reason is preferred when there is one.
    /// </summary>
    private static string DescribeFailure(Exception ex, string reasonFormat) =>
        IsAccessDenied(ex) || IsAccessDenied(ex.InnerException)
            ? NoPermissionReason
            : string.Format(CultureInfo.InvariantCulture, reasonFormat, WindowsReason(ex).TrimEnd(SentenceEnd));

    private static string WindowsReason(Exception ex) =>
        ex.InnerException is Win32Exception windows && !string.IsNullOrWhiteSpace(windows.Message)
            ? windows.Message
            : ex.Message;

    private static bool IsAccessDenied(Exception? ex) =>
        ex is Win32Exception { NativeErrorCode: AccessDeniedError };

    private static bool IsAlreadyRunning(Exception? ex) =>
        ex is Win32Exception { NativeErrorCode: ServiceAlreadyRunningError };

    /// <summary>
    ///     Pings each database still silent, one round at a time, until all answer, one
    ///     refuses, or the deadline passes. Returns the reason the attempt failed, or null
    ///     when every database answered.
    ///     <para>
    ///         A ping that fails is expected while MongoDB starts: it cannot be reached yet,
    ///         the driver's connection pool is still paused, or the server says it is
    ///         recovering or shutting down. Waiting goes on for all of those. Only a rejected
    ///         login (an authentication failure) means MongoDB answered and refused; waiting
    ///         longer would not change that, so the attempt ends at once with that reason
    ///         instead of waiting out the full restart wait and blaming a silent service.
    ///     </para>
    /// </summary>
    private async Task<(string? Reason, Exception? Error)> WaitForAnswersAsync(
        IReadOnlyList<IRecoverableDatabase> down,
        RecoveredServiceState observed,
        CancellationToken deadline)
    {
        var waiting = down.ToList();
        Exception? refusal = null;
        bool deadlinePassed = false;
        while (waiting.Count > 0 && !deadlinePassed && refusal == null)
        {
            var silent = new List<IRecoverableDatabase>();
            foreach (var database in waiting)
            {
                var failure = await PingAsync(database, deadline);
                if (failure != null)
                    silent.Add(database);
                if (failure != null && refusal == null && IsRefusal(failure))
                    refusal = failure;
            }

            waiting = silent;
            if (waiting.Count > 0 && refusal == null)
                deadlinePassed = !await DelayAsync(mTimings.PingInterval, deadline);
        }

        (string? Reason, Exception? Error) result = (null, null);
        if (refusal != null)
            result = (string.Format(CultureInfo.InvariantCulture, RefusedReason, refusal.Message.TrimEnd(SentenceEnd)),
                      refusal);
        if (refusal == null && waiting.Count > 0)
            result = (DescribeSilence(observed), null);

        return result;
    }

    private string DescribeSilence(RecoveredServiceState observed)
    {
        string seconds = mTimings.RestartWait.TotalSeconds.ToString(SecondsFormat, CultureInfo.InvariantCulture);
        string reason = string.Format(CultureInfo.InvariantCulture,
                                      observed == RecoveredServiceState.StartedBySaddleRag
                                          ? DidNotAnswerReason
                                          : RunningNotAnsweringReason,
                                      seconds
                                     );
        return reason;
    }

    /// <summary>
    ///     True only for an authentication failure: MongoDB answered and rejected the
    ///     login, and waiting will not change that. Every other failure (an unreachable
    ///     server, a paused connection pool, a node that is recovering, a cancelled ping)
    ///     is just MongoDB not being ready yet. The failure may be wrapped as an inner
    ///     exception or inside an <see cref="AggregateException" />.
    /// </summary>
    private static bool IsRefusal(Exception? failure) =>
        failure switch
            {
                null => false,
                MongoAuthenticationException => true,
                AggregateException aggregate => aggregate.InnerExceptions.Any(IsRefusal),
                var _ => IsRefusal(failure.InnerException)
            };

    /// <summary>
    ///     Returns null when the database answered, or why the ping failed. A failed ping is
    ///     the expected answer while MongoDB starts, so it is neither thrown nor logged; the
    ///     attempt's own outcome is logged once.
    /// </summary>
    private async Task<Exception?> PingAsync(IRecoverableDatabase database, CancellationToken deadline)
    {
        using var timeout = new CancellationTokenSource(mTimings.PingTimeout, mTimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline, timeout.Token);
        Task ping;
        try
        {
            ping = database.CheckHealthAsync(linked.Token);
        }
        catch(Exception ex)
        {
            // A check that throws before returning its task is still just a failed ping;
            // the attempt must never end in anything but its outcome.
            ping = Task.FromException(ex);
        }

        await ping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Exception? failure = null;
        if (ping.IsFaulted)
            failure = ping.Exception?.InnerException ?? ping.Exception;
        if (ping.IsCanceled)
            failure = new TaskCanceledException(ping);

        return failure;
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
    private const int ServiceAlreadyRunningError = 1056;
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
    private const string UnexpectedFailureReason = "the attempt failed unexpectedly: {0}";
    private const string RefusedReason = "MongoDB answered but refused the request: {0}";
    private const string DidNotAnswerReason = "it did not answer within {0} seconds";

    private const string RunningNotAnsweringReason =
        "the service is running but did not answer within {0} seconds; SaddleRAG does not stop a running service";

    private const string StartedAction = "SaddleRAG started the '{0}' service.";
    private const string WasStartingAction = "The '{0}' service was already starting; SaddleRAG waited for it.";

    private const string WasRunningAction =
        "The '{0}' service was already running; SaddleRAG waited for it to answer again.";
}
