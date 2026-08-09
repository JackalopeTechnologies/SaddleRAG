// StartAndMonitorCommand.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Diagnostics;

namespace SaddleRAG.Installer.Helper;

/// <summary>
///     Starts the MSI-registered SaddleRAG service with bounded retries and
///     waits for its HTTP health endpoint.
/// </summary>
public sealed class StartAndMonitorCommand
{
    public StartAndMonitorCommand(IInstallerProcessRunner processRunner,
                                  IWindowsServiceController serviceController,
                                  IInstallerHealthProbe healthProbe)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(serviceController);
        ArgumentNullException.ThrowIfNull(healthProbe);
        mProcessRunner = processRunner;
        mServiceController = serviceController;
        mHealthProbe = healthProbe;
    }

    private readonly IInstallerHealthProbe mHealthProbe;
    private readonly IInstallerProcessRunner mProcessRunner;
    private readonly IWindowsServiceController mServiceController;

    public async Task<StartAndMonitorResult> RunAsync(StartAndMonitorOptions options,
                                                      CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ProcessExecutionResult initialQuery = await mProcessRunner.RunAsync(
                                                  CreateQueryInvocation(options.ServiceName),
                                                  cancellationToken);
        StartAndMonitorResult result;
        if (initialQuery.ExitCode != SuccessExitCode)
        {
            result = FromProcessFailure(initialQuery);
        }
        else
        {
            if (!File.Exists(options.BinaryPath))
            {
                result = new StartAndMonitorResult(false,
                                                   string.Empty,
                                                   $"The installed service binary was not found at '{options.BinaryPath}'.");
            }
            else
            {
                result = await MonitorAsync(options, initialQuery, cancellationToken);
            }
        }

        return result;
    }

    private async Task<StartAndMonitorResult> MonitorAsync(StartAndMonitorOptions options,
                                                           ProcessExecutionResult initialQuery,
                                                           CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var healthy = false;
        var startAttempts = 0;
        var firstObservation = true;
        StartAndMonitorResult? failure = null;
        while (!healthy && failure == null && stopwatch.Elapsed < options.TotalTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessExecutionResult query;
            if (firstObservation)
            {
                query = initialQuery;
                firstObservation = false;
            }
            else
            {
                query = await mServiceController.QueryAsync(options.ServiceName,
                                                             cancellationToken);
            }

            if (query.ExitCode != SuccessExitCode)
            {
                failure = FromProcessFailure(query);
            }
            else
            {
                WindowsServiceState state = ParseState(query.StandardOutput);
                if (state == WindowsServiceState.Running)
                {
                    healthy = await mHealthProbe.IsHealthyAsync(options.HealthUrl,
                                                                options.HealthRequestTimeout,
                                                                cancellationToken);
                }
                else
                {
                    failure = await HandleNonRunningStateAsync(state,
                                                               options,
                                                               startAttempts,
                                                               cancellationToken);
                    startAttempts = CountStartAttempt(state, failure, startAttempts);
                }
            }

            if (!healthy && failure == null && stopwatch.Elapsed < options.TotalTimeout)
                await Task.Delay(options.PollInterval, cancellationToken);
        }

        StartAndMonitorResult result;
        if (failure != null)
        {
            result = failure;
        }
        else
        {
            result = healthy
                ? new StartAndMonitorResult(true,
                                            $"{options.ServiceName} is running and healthy.",
                                            string.Empty)
                : new StartAndMonitorResult(false,
                                            string.Empty,
                                            $"{options.ServiceName} did not become healthy within {options.TotalTimeout.TotalSeconds:N0} seconds.");
        }

        return result;
    }

    private async Task<StartAndMonitorResult?> HandleNonRunningStateAsync(
        WindowsServiceState state,
        StartAndMonitorOptions options,
        int startAttempts,
        CancellationToken cancellationToken)
    {
        StartAndMonitorResult? result = null;
        if (state == WindowsServiceState.Stopped)
        {
            if (startAttempts >= options.MaxStartAttempts)
            {
                result = new StartAndMonitorResult(false,
                                                   string.Empty,
                                                   $"{options.ServiceName} exceeded {options.MaxStartAttempts} start attempts.");
            }
            else
            {
                ProcessExecutionResult started = await mServiceController.StartAsync(options.ServiceName,
                                                                                      cancellationToken);
                if (started.ExitCode != SuccessExitCode)
                    result = FromProcessFailure(started);
            }
        }
        else
        {
            if (state == WindowsServiceState.Unknown)
            {
                result = new StartAndMonitorResult(false,
                                                   string.Empty,
                                                   $"{options.ServiceName} returned an unrecognized service state.");
            }
        }

        return result;
    }

    private static ProcessInvocation CreateQueryInvocation(string serviceName) =>
        new(ServiceControlExecutable,
            [QueryCommand, serviceName],
            smNativeCommandTimeout);

    private static int CountStartAttempt(WindowsServiceState state,
                                         StartAndMonitorResult? failure,
                                         int startAttempts)
    {
        var result = startAttempts;
        if (state == WindowsServiceState.Stopped && failure == null)
            result++;
        return result;
    }

    private static WindowsServiceState ParseState(string output)
    {
        WindowsServiceState result = WindowsServiceState.Unknown;
        if (output.Contains(RunningState, StringComparison.OrdinalIgnoreCase))
            result = WindowsServiceState.Running;
        if (result == WindowsServiceState.Unknown
            && output.Contains(StartPendingState, StringComparison.OrdinalIgnoreCase))
            result = WindowsServiceState.StartPending;
        if (result == WindowsServiceState.Unknown
            && output.Contains(StopPendingState, StringComparison.OrdinalIgnoreCase))
            result = WindowsServiceState.StopPending;
        if (result == WindowsServiceState.Unknown
            && output.Contains(StoppedState, StringComparison.OrdinalIgnoreCase))
            result = WindowsServiceState.Stopped;

        return result;
    }

    private static StartAndMonitorResult FromProcessFailure(ProcessExecutionResult result) =>
        new(false, result.StandardOutput, result.StandardError);

    private static readonly TimeSpan smNativeCommandTimeout = TimeSpan.FromSeconds(30);

    private const int SuccessExitCode = 0;
    private const string ServiceControlExecutable = "sc.exe";
    private const string QueryCommand = "query";
    private const string RunningState = "RUNNING";
    private const string StartPendingState = "START_PENDING";
    private const string StopPendingState = "STOP_PENDING";
    private const string StoppedState = "STOPPED";
}
