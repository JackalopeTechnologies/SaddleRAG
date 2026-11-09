// DatabaseRecoveryTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>
///     Pins how SaddleRAG restarts a stopped local MongoDB service on demand: one shared
///     attempt, a bounded wait, a pause after a failure, and never stopping a service.
///     Timings are short, and assertions are on counts and outcomes, not on how long
///     anything took.
/// </summary>
public sealed class DatabaseRecoveryTests
{
    [Fact]
    public async Task NothingKnownDownDoesNotTouchTheService()
    {
        var service = new FakeServiceControl(MongoServiceState.Stopped);
        var database = new FakeDatabase(isLocal: true, isKnownDown: false);
        var recovery = CreateRecovery(service, database, new RecordingLogger());

        await recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 0, service.GetStateCount);
    }

    [Fact]
    public async Task RemoteDatabaseDownDoesNotRestart()
    {
        var service = new FakeServiceControl(MongoServiceState.Stopped);
        var database = new FakeDatabase(isLocal: false, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger());

        await recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 0, service.GetStateCount);
        Assert.Equal(expected: 0, service.StartCount);
    }

    [Fact]
    public async Task DisabledDoesNotRestart()
    {
        var service = new FakeServiceControl(MongoServiceState.Stopped);
        var database = new FakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger(), enabled: false);

        await recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 0, service.GetStateCount);
        Assert.Equal(expected: 0, service.StartCount);
    }

    [Fact]
    public async Task StoppedServiceIsStartedAndTheCallerContinues()
    {
        var database = new FakeDatabase(isLocal: true, isKnownDown: true);
        var service = new FakeServiceControl(MongoServiceState.Stopped) { OnStart = database.BeginAnswering };
        var logger = new RecordingLogger();
        var recovery = CreateRecovery(service, database, logger);

        await recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 1, service.StartCount);
        Assert.Equal(expected: 1, logger.Count(LogLevel.Warning));
        Assert.Equal(expected: 0, logger.Count(LogLevel.Error));
    }

    [Fact]
    public async Task ConcurrentCallersShareOneAttempt()
    {
        var database = new FakeDatabase(isLocal: true, isKnownDown: true);
        var service = new FakeServiceControl(MongoServiceState.Stopped) { OnStart = database.BeginAnswering };
        var recovery = CreateRecovery(service, database, new RecordingLogger());
        var ct = TestContext.Current.CancellationToken;

        var callers = Enumerable.Range(start: 0, ConcurrentCallerCount)
                                .Select(_ => Task.Run(() => recovery.EnsureAvailableAsync(ct), ct))
                                .ToList();
        await Task.WhenAll(callers);

        Assert.Equal(expected: 1, service.StartCount);
        Assert.All(callers, caller => Assert.True(caller.IsCompletedSuccessfully));
    }

    [Fact]
    public async Task ServiceNeverAnswersFailsWithTheReport()
    {
        var service = new FakeServiceControl(MongoServiceState.Stopped);
        var database = new FakeDatabase(isLocal: true, isKnownDown: true);
        var logger = new RecordingLogger();
        var recovery = CreateRecovery(service, database, logger);

        var failure = await Assert.ThrowsAsync<DatabaseUnavailableException>(
                          () => recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken));

        Assert.Contains(DidNotAnswerProbe, failure.RestartReport, StringComparison.Ordinal);
        Assert.Equal(expected: 1, logger.Count(LogLevel.Error));
    }

    [Fact]
    public async Task WithinThePauseFailsImmediatelyWithoutANewAttempt()
    {
        var service = new FakeServiceControl(MongoServiceState.Stopped);
        var database = new FakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger());
        var ct = TestContext.Current.CancellationToken;

        var first = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => recovery.EnsureAvailableAsync(ct));
        var second = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => recovery.EnsureAvailableAsync(ct));

        Assert.Equal(first.RestartReport, second.RestartReport);
        Assert.Equal(expected: 1, service.StartCount);
    }

    [Fact]
    public async Task AfterThePauseTheNextCallTriesAgain()
    {
        var service = new FakeServiceControl(MongoServiceState.Stopped) { StopsAgainAfterStart = true };
        var database = new FakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger());
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => recovery.EnsureAvailableAsync(ct));
        await Task.Delay(smWellPastThePause, ct);
        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => recovery.EnsureAvailableAsync(ct));

        Assert.Equal(expected: 2, service.StartCount);
    }

    [Fact]
    public async Task AccessDeniedReportsPermission()
    {
        var service = new FakeServiceControl(MongoServiceState.Stopped)
                          {
                              StartFailure = new InvalidOperationException("x", new Win32Exception(AccessDeniedError))
                          };
        var database = new FakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger());

        var failure = await Assert.ThrowsAsync<DatabaseUnavailableException>(
                          () => recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken));

        Assert.Contains(PermissionProbe, failure.RestartReport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotInstalledReportsIt()
    {
        var service = new FakeServiceControl(MongoServiceState.NotInstalled);
        var database = new FakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger());

        var failure = await Assert.ThrowsAsync<DatabaseUnavailableException>(
                          () => recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken));

        Assert.Contains(NotInstalledProbe, failure.RestartReport, StringComparison.Ordinal);
        Assert.Equal(expected: 0, service.StartCount);
    }

    [Fact]
    public async Task UnreadableServiceStateReportsIt()
    {
        var service = new FakeServiceControl(MongoServiceState.Stopped)
                          {
                              GetStateFailure = new InvalidOperationException(UnreadableStateMessage)
                          };
        var database = new FakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger());

        var failure = await Assert.ThrowsAsync<DatabaseUnavailableException>(
                          () => recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken));

        Assert.Contains(UnreadableStateProbe, failure.RestartReport, StringComparison.Ordinal);
        Assert.Contains(UnreadableStateMessage, failure.RestartReport, StringComparison.Ordinal);
        Assert.Equal(expected: 0, service.StartCount);
    }

    [Fact]
    public async Task AccessDeniedReadingTheStateReportsPermission()
    {
        var service = new FakeServiceControl(MongoServiceState.Stopped)
                          {
                              GetStateFailure = new InvalidOperationException("x", new Win32Exception(AccessDeniedError))
                          };
        var database = new FakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger());

        var failure = await Assert.ThrowsAsync<DatabaseUnavailableException>(
                          () => recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken));

        Assert.Contains(PermissionProbe, failure.RestartReport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningButNotAnsweringIsNeverStopped()
    {
        var service = new FakeServiceControl(MongoServiceState.Running);
        var database = new FakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger());

        var failure = await Assert.ThrowsAsync<DatabaseUnavailableException>(
                          () => recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected: 0, service.StartCount);
        Assert.Contains(NeverStopsProbe, failure.RestartReport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopPendingWaitsThenStarts()
    {
        var database = new FakeDatabase(isLocal: true, isKnownDown: true);
        var service = new FakeServiceControl(MongoServiceState.StopPending,
                                             MongoServiceState.StopPending,
                                             MongoServiceState.Stopped)
                          {
                              OnStart = database.BeginAnswering
                          };
        var recovery = CreateRecovery(service, database, new RecordingLogger());

        await recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 1, service.StartCount);
    }

    [Fact]
    public async Task ACancelledCallerDoesNotCancelTheSharedAttempt()
    {
        var database = new FakeDatabase(isLocal: true, isKnownDown: true);
        var service = new FakeServiceControl(MongoServiceState.Stopped);
        var timings = smTestTimings with { RestartWait = smLongRestartWait };
        var recovery = CreateRecovery(service, database, new RecordingLogger(), timings: timings);
        var ct = TestContext.Current.CancellationToken;
        using var callerA = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var taskA = recovery.EnsureAvailableAsync(callerA.Token);
        var taskB = recovery.EnsureAvailableAsync(ct);
        await callerA.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskA);
        database.BeginAnswering();
        await taskB;

        Assert.True(taskB.IsCompletedSuccessfully);
        Assert.Equal(expected: 1, service.StartCount);
    }

    [Fact]
    public async Task AFailureNoCallerWaitsForIsNotLeftUnobserved()
    {
        var service = new FakeServiceControl(MongoServiceState.Stopped);
        var database = new FakeDatabase(isLocal: true, isKnownDown: true);
        var logger = new RecordingLogger();
        var recovery = CreateRecovery(service, database, logger, serviceName: UnobservedProbeServiceName);
        var ct = TestContext.Current.CancellationToken;
        int unobserved = 0;

        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(IsFromThisTest))
                Interlocked.Increment(ref unobserved);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await CancelTheOnlyCallerAsync(recovery, ct);
            await WaitForErrorAsync(logger, ct);
            await Task.Delay(smSettleDelay, ct);
            CollectGarbage();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.Equal(expected: 1, logger.Count(LogLevel.Error));
        Assert.Equal(expected: 0, Volatile.Read(ref unobserved));
    }

    [Fact]
    public async Task RecoveryPingRunsOutsideTheFailFastScope()
    {
        var database = new FakeDatabase(isLocal: true, isKnownDown: true);
        var service = new FakeServiceControl(MongoServiceState.Stopped) { OnStart = database.BeginAnswering };
        var recovery = CreateRecovery(service, database, new RecordingLogger());

        using (DatabaseFailFastScope.Begin())
        {
            await recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(database.PingCount > 0);
        Assert.False(database.SawFailFastScope);
    }

    [Fact]
    public async Task ContextReportsKnownDownAfterAFailedHealthCheck()
    {
        var settings = UnreachableDatabase.CreateSettings(out string _, ShortSelectionTimeoutMilliseconds);
        IRecoverableDatabase context = new SaddleRagDbContext(Options.Create(settings));

        await Assert.ThrowsAnyAsync<Exception>(() => context.CheckHealthAsync(TestContext.Current.CancellationToken));

        Assert.True(context.IsKnownDown);
        Assert.True(context.IsLocal);
        Assert.False(string.IsNullOrEmpty(context.Endpoint));
    }

    private static bool IsFromThisTest(Exception ex) =>
        ex is DatabaseUnavailableException { RestartReport: { } report } &&
        report.Contains(UnobservedProbeServiceName, StringComparison.Ordinal);

    private static async Task CancelTheOnlyCallerAsync(DatabaseRecovery recovery, CancellationToken ct)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var waiting = recovery.EnsureAvailableAsync(caller.Token);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    private static async Task WaitForErrorAsync(RecordingLogger logger, CancellationToken ct)
    {
        while (logger.Count(LogLevel.Error) == 0)
            await Task.Delay(smErrorPollInterval, ct);
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static DatabaseRecovery CreateRecovery(FakeServiceControl service,
                                                   FakeDatabase database,
                                                   RecordingLogger logger,
                                                   bool enabled = true,
                                                   DatabaseRecoveryTimings? timings = null,
                                                   string serviceName = ServiceName)
    {
        var settings = new SaddleRagDbSettings
                           {
                               AutoRestart = new MongoAutoRestartSettings { Enabled = enabled, ServiceName = serviceName }
                           };
        IReadOnlyList<IRecoverableDatabase> databases = [database];
        var recovery = new DatabaseRecovery(() => databases,
                                            service,
                                            Options.Create(settings),
                                            TimeProvider.System,
                                            logger,
                                            timings ?? smTestTimings
                                           );
        return recovery;
    }

    private const int ConcurrentCallerCount = 10;
    private const int AccessDeniedError = 5;
    private const int ShortSelectionTimeoutMilliseconds = 500;
    private const string ServiceName = "MongoDB";
    private const string UnobservedProbeServiceName = "MongoDB_UnobservedProbe_5c1e";
    private const string DidNotAnswerProbe = "did not answer within";
    private const string PermissionProbe = "does not have permission";
    private const string NotInstalledProbe = "is not installed";
    private const string NeverStopsProbe = "does not stop a running service";
    private const string UnreadableStateProbe = "could not read the service's state";
    private const string UnreadableStateMessage = "the service manager is busy";

    private static readonly DatabaseRecoveryTimings smTestTimings =
        new DatabaseRecoveryTimings(RestartWait: TimeSpan.FromMilliseconds(milliseconds: 300),
                                    FailurePause: TimeSpan.FromMilliseconds(milliseconds: 500),
                                    PingTimeout: TimeSpan.FromMilliseconds(milliseconds: 50),
                                    PingInterval: TimeSpan.FromMilliseconds(milliseconds: 20)
                                   );

    private static readonly TimeSpan smWellPastThePause = TimeSpan.FromMilliseconds(milliseconds: 900);
    private static readonly TimeSpan smLongRestartWait = TimeSpan.FromSeconds(seconds: 30);
    private static readonly TimeSpan smErrorPollInterval = TimeSpan.FromMilliseconds(milliseconds: 10);
    private static readonly TimeSpan smSettleDelay = TimeSpan.FromMilliseconds(milliseconds: 100);

    /// <summary>A Windows service whose reported states and start outcome the test chooses.</summary>
    private sealed class FakeServiceControl : IMongoServiceControl
    {
        public FakeServiceControl(MongoServiceState initial, params MongoServiceState[] laterStates)
        {
            mState = initial;
            mPending = new Queue<MongoServiceState>([initial, .. laterStates]);
        }

        private readonly Lock mLock = new Lock();
        private readonly Queue<MongoServiceState> mPending;
        private int mGetStateCount;
        private int mStartCount;
        private MongoServiceState mState;

        public Action? OnStart { get; init; }

        public Exception? StartFailure { get; init; }

        public Exception? GetStateFailure { get; init; }

        /// <summary>The service accepts a start and then stops again, as one that crashes on start does.</summary>
        public bool StopsAgainAfterStart { get; init; }

        public int GetStateCount => Volatile.Read(ref mGetStateCount);

        public int StartCount => Volatile.Read(ref mStartCount);

        public MongoServiceState GetState(string serviceName)
        {
            Interlocked.Increment(ref mGetStateCount);
            if (GetStateFailure != null)
                throw GetStateFailure;

            MongoServiceState result;
            lock(mLock)
            {
                if (mPending.Count > 0)
                    mState = mPending.Dequeue();
                result = mState;
            }

            return result;
        }

        public void Start(string serviceName)
        {
            Interlocked.Increment(ref mStartCount);
            if (StartFailure != null)
                throw StartFailure;

            lock(mLock)
            {
                mPending.Clear();
                mState = StopsAgainAfterStart ? MongoServiceState.Stopped : MongoServiceState.Running;
            }

            OnStart?.Invoke();
        }
    }

    /// <summary>
    ///     A database that refuses pings until <see cref="BeginAnswering" />, and, like the
    ///     real context, stops being known down once a ping succeeds.
    /// </summary>
    private sealed class FakeDatabase : IRecoverableDatabase
    {
        public FakeDatabase(bool isLocal, bool isKnownDown)
        {
            IsLocal = isLocal;
            mIsKnownDown = isKnownDown;
        }

        private volatile bool mAnswering;
        private volatile bool mIsKnownDown;
        private int mPingCount;
        private volatile bool mSawFailFastScope;

        public int PingCount => Volatile.Read(ref mPingCount);

        public bool SawFailFastScope => mSawFailFastScope;

        public string Endpoint => "localhost:27017";

        public bool IsLocal { get; }

        public bool IsKnownDown => mIsKnownDown;

        public Task CheckHealthAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref mPingCount);
            if (DatabaseFailFastScope.IsActive)
                mSawFailFastScope = true;

            Task result;
            if (mAnswering)
            {
                mIsKnownDown = false;
                result = Task.CompletedTask;
            }
            else
                result = Task.FromException(new TimeoutException("No answer."));

            return result;
        }

        public void BeginAnswering() => mAnswering = true;
    }

    /// <summary>Counts what <see cref="DatabaseRecovery" /> logs at each level.</summary>
    private sealed class RecordingLogger : ILogger<DatabaseRecovery>
    {
        private readonly Lock mLock = new Lock();
        private readonly List<LogLevel> mLevels = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel,
                                EventId eventId,
                                TState state,
                                Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            lock(mLock)
            {
                mLevels.Add(logLevel);
            }
        }

        public int Count(LogLevel level)
        {
            int result;
            lock(mLock)
            {
                result = mLevels.Count(l => l == level);
            }

            return result;
        }
    }
}
