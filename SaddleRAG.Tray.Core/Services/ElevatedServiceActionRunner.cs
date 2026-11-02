// ElevatedServiceActionRunner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Starts, stops or restarts named Windows services by running the installed
///     <c>Restart-SaddleRAGComponent.ps1</c> elevated. The tray itself stays unelevated, and
///     the script decides which services it will touch.
///     <para>
///         Only the installed script is ever elevated, by absolute path, and only after the
///         install location has been confirmed to be changeable by administrators alone. No
///         caller-supplied text reaches the command line except service names, which are
///         restricted to characters that cannot add an argument.
///     </para>
///     <para>
///         Success and an operator who declines the Windows prompt log at Information; a denied,
///         failed or timed-out action logs at Warning. Error is reserved for exceptions this
///         class does not expect, which propagate to the caller.
///     </para>
/// </summary>
public sealed partial class ElevatedServiceActionRunner : IServiceActionRunner
{
    public ElevatedServiceActionRunner(IElevatedProcessLauncher launcher,
                                       string scriptPath,
                                       IFileSystemProbe probe,
                                       IInstallLocationGuard guard,
                                       ILogger<ElevatedServiceActionRunner>? logger = null,
                                       TimeSpan? processWaitTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(guard);

        mLauncher = launcher;
        mScriptPath = scriptPath;
        mProbe = probe;
        mGuard = guard;
        mLogger = logger;
        mProcessWaitTimeout = processWaitTimeout ?? DefaultProcessWaitTimeout;
    }

    /// <summary>The script's wait for each service to reach its state; passed to it explicitly.</summary>
    public const int ScriptTimeoutSeconds = 120;

    /// <summary>
    ///     A restart can wait out a starting service, stop it, then start it, each bounded by the
    ///     script timeout. The tray waits for that many, plus a margin, before giving up on hearing
    ///     back.
    /// </summary>
    public const int MaxScriptWaitsPerAction = 3;

    public const int ProcessWaitMarginSeconds = 30;

    public static TimeSpan DefaultProcessWaitTimeout =>
        TimeSpan.FromSeconds((MaxScriptWaitsPerAction * ScriptTimeoutSeconds) + ProcessWaitMarginSeconds);

    private const int ExitSucceeded = 0;
    private const int ExitNotAllowed = 10;
    private const int ExitNotInstalled = 11;
    private const int ExitTimedOut = 12;
    private const int ExitFailed = 13;

    // ERROR_CANCELLED: what Windows reports when the operator declines the administrator prompt.
    private const int ElevationDeclinedErrorCode = 1223;

    // The first character cannot be a hyphen, so a name can never be read as a parameter.
    private const string ServiceNamePattern = @"^[A-Za-z0-9_.][A-Za-z0-9_.-]*\z";
    private const string PowerShellFolder = "WindowsPowerShell";
    private const string PowerShellVersionFolder = "v1.0";
    private const string PowerShellExecutable = "powershell.exe";
    private const string RunAsVerb = "runas";

    private const string InvalidNameMessage =
        "Service names may contain only letters, digits, underscore, dot and hyphen, and may not start with a hyphen.";
    private const string NoNamesMessage = "At least one service name is required.";

    private const string SucceededLogMessage = "Service action {Action} on {Services} succeeded";
    private const string CancelledLogMessage =
        "Service action {Action} on {Services} was cancelled: Windows did not get administrator approval";
    private const string ScriptMissingLogMessage =
        "Service action {Action} on {Services} not run: the helper script {Script} is missing";
    private const string UnsafeLogMessage =
        "Service action {Action} on {Services} refused: install location of {Script} is not protected: {Reason}";
    private const string LaunchFailedLogMessage =
        "Service action {Action} on {Services} could not start the elevated script {Script}";
    private const string TimedOutWaitingLogMessage =
        "Service action {Action} on {Services}: gave up waiting for the elevated script after {Timeout}";
    private const string ExitCodeLogMessage =
        "Service action {Action} on {Services} ended with {Outcome} (script exit code {ExitCode})";

    private readonly IElevatedProcessLauncher mLauncher;
    private readonly string mScriptPath;
    private readonly IFileSystemProbe mProbe;
    private readonly IInstallLocationGuard mGuard;
    private readonly ILogger<ElevatedServiceActionRunner>? mLogger;
    private readonly TimeSpan mProcessWaitTimeout;

    [GeneratedRegex(ServiceNamePattern, RegexOptions.CultureInvariant)]
    private static partial Regex ServiceNameRegex();

    /// <summary>
    ///     Runs <paramref name="action" /> on the named services, in the order given, and reports how
    ///     it ended. Throws <see cref="ArgumentException" /> for a name outside the permitted
    ///     characters, before anything is launched. When <paramref name="ct" /> is cancelled the
    ///     wait ends with <see cref="OperationCanceledException" />; the elevated script is left to
    ///     finish its work.
    /// </summary>
    public async Task<ServiceActionOutcome> RunAsync(IReadOnlyList<string> serviceNames,
                                                     ServiceAction action,
                                                     CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(serviceNames);

        ValidateNames(serviceNames);
        string services = string.Join(',', serviceNames);

        ServiceActionOutcome result;
        if (!mProbe.FileExists(mScriptPath))
        {
            mLogger?.LogWarning(ScriptMissingLogMessage, action, services, mScriptPath);
            result = ServiceActionOutcome.ScriptMissing;
        }
        else
        {
            InstallLocationVerdict verdict = mGuard.Check(mScriptPath);
            if (verdict.IsSafe)
            {
                result = await LaunchAsync(services, action, ct);
            }
            else
            {
                mLogger?.LogWarning(UnsafeLogMessage, action, services, mScriptPath, verdict.Reason);
                result = ServiceActionOutcome.UnsafeInstallLocation;
            }
        }

        return result;
    }

    private async Task<ServiceActionOutcome> LaunchAsync(string services, ServiceAction action, CancellationToken ct)
    {
        ServiceActionOutcome result;
        try
        {
            int exitCode = await mLauncher.RunAsync(BuildStartInfo(services, action), mProcessWaitTimeout, ct);
            result = MapExitCode(exitCode);
            LogExit(action, services, result, exitCode);
        }
        catch(Win32Exception ex) when (ex.NativeErrorCode == ElevationDeclinedErrorCode)
        {
            mLogger?.LogInformation(CancelledLogMessage, action, services);
            result = ServiceActionOutcome.Cancelled;
        }
        catch(Win32Exception ex)
        {
            mLogger?.LogWarning(ex, LaunchFailedLogMessage, action, services, mScriptPath);
            result = ServiceActionOutcome.Failed;
        }
        catch(TimeoutException)
        {
            // The wait limit ran out. The elevated script is still running and will finish its
            // service action on its own; the tray just stops waiting to hear back.
            mLogger?.LogWarning(TimedOutWaitingLogMessage, action, services, mProcessWaitTimeout);
            result = ServiceActionOutcome.TimedOut;
        }

        return result;
    }

    private ProcessStartInfo BuildStartInfo(string services, ServiceAction action)
    {
        string powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                         PowerShellFolder,
                                         PowerShellVersionFolder,
                                         PowerShellExecutable);
        string arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{mScriptPath}\" "
                           + $"-ServiceName {services} -Action {action} -TimeoutSeconds {ScriptTimeoutSeconds}";
        return new ProcessStartInfo
               {
                   FileName = powerShell,
                   Arguments = arguments,
                   Verb = RunAsVerb,
                   UseShellExecute = true,
                   WindowStyle = ProcessWindowStyle.Hidden
               };
    }

    private static void ValidateNames(IReadOnlyList<string> serviceNames)
    {
        if (serviceNames.Count == 0)
            throw new ArgumentException(NoNamesMessage, nameof(serviceNames));

        foreach (string name in serviceNames)
        {
            if (string.IsNullOrEmpty(name) || !ServiceNameRegex().IsMatch(name))
                throw new ArgumentException(InvalidNameMessage, nameof(serviceNames));
        }
    }

    private static ServiceActionOutcome MapExitCode(int exitCode) => exitCode switch
                                                                     {
                                                                         ExitSucceeded => ServiceActionOutcome.Succeeded,
                                                                         ExitNotAllowed => ServiceActionOutcome.NotAllowed,
                                                                         ExitNotInstalled => ServiceActionOutcome.NotInstalled,
                                                                         ExitTimedOut => ServiceActionOutcome.TimedOut,
                                                                         ExitFailed => ServiceActionOutcome.Failed,
                                                                         // Anything else, such as 1 from a PowerShell
                                                                         // parameter-binding error, is a failure.
                                                                         _ => ServiceActionOutcome.Failed
                                                                     };

    private void LogExit(ServiceAction action, string services, ServiceActionOutcome outcome, int exitCode)
    {
        if (outcome == ServiceActionOutcome.Succeeded)
            mLogger?.LogInformation(SucceededLogMessage, action, services);
        else
            mLogger?.LogWarning(ExitCodeLogMessage, action, services, outcome, exitCode);
    }
}
