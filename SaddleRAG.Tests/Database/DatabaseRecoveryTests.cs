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
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped);
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: false);
        var recovery = CreateRecovery(service, database, new RecordingLogger<DatabaseRecovery>());

        await recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 0, service.GetStateCount);
    }

    [Fact]
    public async Task RemoteDatabaseDownDoesNotRestart()
    {
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped);
        var database = new RecoveryFakeDatabase(isLocal: false, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger<DatabaseRecovery>());

        await recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 0, service.GetStateCount);
        Assert.Equal(expected: 0, service.StartCount);
    }

    [Fact]
    public async Task DisabledDoesNotRestart()
    {
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped);
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger<DatabaseRecovery>(), enabled: false);

        await recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 0, service.GetStateCount);
        Assert.Equal(expected: 0, service.StartCount);
    }

    [Fact]
    public async Task StoppedServiceIsStartedAndTheCallerContinues()
    {
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped) { OnStart = database.BeginAnswering };
        var logger = new RecordingLogger<DatabaseRecovery>();
        var recovery = CreateRecovery(service, database, logger);

        await recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 1, service.StartCount);
        Assert.Equal(expected: 1, logger.Count(LogLevel.Warning));
        Assert.Equal(expected: 0, logger.Count(LogLevel.Error));
    }

    [Fact]
    public async Task ConcurrentCallersShareOneAttempt()
    {
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped) { OnStart = database.BeginAnswering };
        var recovery = CreateRecovery(service, database, new RecordingLogger<DatabaseRecovery>());
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
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped);
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var logger = new RecordingLogger<DatabaseRecovery>();
        var recovery = CreateRecovery(service, database, logger);

        var failure = await Assert.ThrowsAsync<DatabaseUnavailableException>(
                          () => recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken));

        Assert.Contains(DidNotAnswerProbe, failure.RestartReport, StringComparison.Ordinal);
        Assert.Equal(expected: 1, logger.Count(LogLevel.Error));
    }

    [Fact]
    public async Task WithinThePauseFailsImmediatelyWithoutANewAttempt()
    {
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped);
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger<DatabaseRecovery>());
        var ct = TestContext.Current.CancellationToken;

        var first = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => recovery.EnsureAvailableAsync(ct));
        var second = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => recovery.EnsureAvailableAsync(ct));

        Assert.Equal(first.RestartReport, second.RestartReport);
        Assert.Equal(expected: 1, service.StartCount);
    }

    [Fact]
    public async Task AfterThePauseTheNextCallTriesAgain()
    {
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped) { StopsAgainAfterStart = true };
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger<DatabaseRecovery>());
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => recovery.EnsureAvailableAsync(ct));
        await Task.Delay(smWellPastThePause, ct);
        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => recovery.EnsureAvailableAsync(ct));

        Assert.Equal(expected: 2, service.StartCount);
    }

    [Fact]
    public async Task AccessDeniedReportsPermission()
    {
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped)
                          {
                              StartFailure = new InvalidOperationException("x", new Win32Exception(AccessDeniedError))
                          };
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger<DatabaseRecovery>());

        var failure = await Assert.ThrowsAsync<DatabaseUnavailableException>(
                          () => recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken));

        Assert.Contains(PermissionProbe, failure.RestartReport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotInstalledReportsIt()
    {
        var service = new RecoveryFakeServiceControl(MongoServiceState.NotInstalled);
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger<DatabaseRecovery>());

        var failure = await Assert.ThrowsAsync<DatabaseUnavailableException>(
                          () => recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken));

        Assert.Contains(NotInstalledProbe, failure.RestartReport, StringComparison.Ordinal);
        Assert.Equal(expected: 0, service.StartCount);
    }

    [Fact]
    public async Task UnreadableServiceStateReportsIt()
    {
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped)
                          {
                              GetStateFailure = new InvalidOperationException(UnreadableStateMessage)
                          };
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger<DatabaseRecovery>());

        var failure = await Assert.ThrowsAsync<DatabaseUnavailableException>(
                          () => recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken));

        Assert.Contains(UnreadableStateProbe, failure.RestartReport, StringComparison.Ordinal);
        Assert.Contains(UnreadableStateMessage, failure.RestartReport, StringComparison.Ordinal);
        Assert.Equal(expected: 0, service.StartCount);
    }

    [Fact]
    public async Task AccessDeniedReadingTheStateReportsPermission()
    {
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped)
                          {
                              GetStateFailure = new InvalidOperationException("x", new Win32Exception(AccessDeniedError))
                          };
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger<DatabaseRecovery>());

        var failure = await Assert.ThrowsAsync<DatabaseUnavailableException>(
                          () => recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken));

        Assert.Contains(PermissionProbe, failure.RestartReport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningButNotAnsweringIsNeverStopped()
    {
        var service = new RecoveryFakeServiceControl(MongoServiceState.Running);
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var recovery = CreateRecovery(service, database, new RecordingLogger<DatabaseRecovery>());

        var failure = await Assert.ThrowsAsync<DatabaseUnavailableException>(
                          () => recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected: 0, service.StartCount);
        Assert.Contains(NeverStopsProbe, failure.RestartReport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopPendingWaitsThenStarts()
    {
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var service = new RecoveryFakeServiceControl(MongoServiceState.StopPending,
                                             MongoServiceState.StopPending,
                                             MongoServiceState.Stopped)
                          {
                              OnStart = database.BeginAnswering
                          };
        var recovery = CreateRecovery(service, database, new RecordingLogger<DatabaseRecovery>());

        await recovery.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 1, service.StartCount);
    }

    [Fact]
    public async Task ACancelledCallerDoesNotCancelTheSharedAttempt()
    {
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped);
        var timings = RecoveryTestTimings.Short with { RestartWait = smLongRestartWait };
        var recovery = CreateRecovery(service, database, new RecordingLogger<DatabaseRecovery>(), timings: timings);
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
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped);
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var logger = new RecordingLogger<DatabaseRecovery>();
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
        var database = new RecoveryFakeDatabase(isLocal: true, isKnownDown: true);
        var service = new RecoveryFakeServiceControl(MongoServiceState.Stopped) { OnStart = database.BeginAnswering };
        var recovery = CreateRecovery(service, database, new RecordingLogger<DatabaseRecovery>());

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

    private static async Task WaitForErrorAsync(RecordingLogger<DatabaseRecovery> logger, CancellationToken ct)
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

    private static DatabaseRecovery CreateRecovery(RecoveryFakeServiceControl service,
                                                   RecoveryFakeDatabase database,
                                                   RecordingLogger<DatabaseRecovery> logger,
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
                                            timings ?? RecoveryTestTimings.Short
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

    private static readonly TimeSpan smWellPastThePause = TimeSpan.FromMilliseconds(milliseconds: 900);
    private static readonly TimeSpan smLongRestartWait = TimeSpan.FromSeconds(seconds: 30);
    private static readonly TimeSpan smErrorPollInterval = TimeSpan.FromMilliseconds(milliseconds: 10);
    private static readonly TimeSpan smSettleDelay = TimeSpan.FromMilliseconds(milliseconds: 100);
}
