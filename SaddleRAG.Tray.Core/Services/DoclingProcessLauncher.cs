// DoclingProcessLauncher.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Starts the Docling command the user registered, from the tray's user session.
///     <para>
///         This deliberately lives in the tray rather than in the ingestion assembly's
///         Docling adapter: the adapter is a pure HTTP client with no lifecycle control,
///         and the MCP service that hosts it runs as LocalSystem. Spawning a user-profile
///         virtualenv from LocalSystem would run it in session 0 under SYSTEM's
///         environment — a different context from the one the user validated.
///     </para>
/// </summary>
public sealed class DoclingProcessLauncher : IDoclingLauncher
{
    public DoclingProcessLauncher(HttpClient httpClient,
                                  ExternalToolRegistry registry,
                                  IProcessStarter processStarter,
                                  IFileSystemProbe probe,
                                  DoclingLaunchSettings settings,
                                  ILogger<DoclingProcessLauncher>? logger = null,
                                  TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(processStarter);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(settings);

        mHttpClient = httpClient;
        mRegistry = registry;
        mProcessStarter = processStarter;
        mProbe = probe;
        mSettings = settings;
        mLogger = logger;
        mTimeProvider = timeProvider ?? TimeProvider.System;
    }

    // Atomic single-shot guard, mirroring OllamaBootstrapper.LaunchOllamaAsync. A transient
    // health-probe failure must not spawn a second server: the failure mode we already saw
    // with Ollama was several processes fighting over one port while the legitimate listener
    // was still loading its models. Only an operator action that found the port free and the
    // launched process gone clears it (AllowRelaunchAfterOperatorStop).
    private static int psLaunchAttempted;

    private readonly HttpClient mHttpClient;
    private readonly ExternalToolRegistry mRegistry;
    private readonly IProcessStarter mProcessStarter;
    private readonly IFileSystemProbe mProbe;
    private readonly DoclingLaunchSettings mSettings;
    private readonly ILogger<DoclingProcessLauncher>? mLogger;
    private readonly TimeProvider mTimeProvider;

    // The process this launcher last started, kept so an operator action can tell a crashed Docling
    // (exited) from one still loading (running). Replaced, and the old handle disposed, on relaunch.
    private readonly Lock mLaunchedSync = new();
    private IDisposable? mLaunched;

    /// <summary>
    ///     True only when this launcher started a process that has not exited. A handle whose state
    ///     cannot be read counts as running, so the guard is kept when in doubt.
    /// </summary>
    public bool LaunchedProcessIsRunning
    {
        get
        {
            bool result;
            lock(mLaunchedSync)
                result = mLaunched is not null && IsRunning(mLaunched);

            return result;
        }
    }

    /// <summary>Resets the process-wide single-shot flag. Tests only; never called in production.</summary>
    internal static void ResetLaunchGuardForTesting() => Interlocked.Exchange(ref psLaunchAttempted, value: 0);

    /// <summary>
    ///     Clears the single-shot guard so the next <see cref="EnsureRunningAsync" /> may start Docling
    ///     again. The guard exists so a transient health-probe failure cannot spawn a second server
    ///     beside one that is still loading. When nothing listens on Docling's port and the process
    ///     this launcher started has exited (the operator stopped it, or it crashed), no server is left
    ///     to collide with, which is the one safe time to clear it. The component controller calls this
    ///     only then, on an operator start or restart: never when a process holds the port, and never
    ///     while the launched process is still running.
    /// </summary>
    public void AllowRelaunchAfterOperatorStop() => Interlocked.Exchange(ref psLaunchAttempted, value: 0);

    /// <summary>
    ///     Ensures Docling is serving, starting the registered command if it is not.
    ///     Never installs, licenses, configures, or upgrades anything.
    /// </summary>
    public async Task<DoclingLaunchOutcome> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        DoclingLaunchOutcome result;
        if (await IsHealthyAsync(cancellationToken))
        {
            result = DoclingLaunchOutcome.AlreadyRunning;
        }
        else
        {
            DoclingRegistration? registration = mRegistry.Read().Docling;
            if (registration == null)
            {
                mLogger?.LogWarning(NotRegisteredMessage);
                result = DoclingLaunchOutcome.NotRegistered;
            }
            else
            {
                result = await StartAndWaitAsync(registration, cancellationToken);
            }
        }

        return result;
    }

    private async Task<DoclingLaunchOutcome> StartAndWaitAsync(DoclingRegistration registration,
                                                               CancellationToken cancellationToken)
    {
        DoclingLaunchOutcome result;
        bool alreadyAttempted = Interlocked.Exchange(ref psLaunchAttempted, value: 1) != 0;
        if (alreadyAttempted)
        {
            mLogger?.LogInformation(AlreadyAttemptedMessage);
            result = await WaitForReadyAsync(cancellationToken);
        }
        else
        {
            result = Start(registration)
                         ? await WaitForReadyAsync(cancellationToken)
                         : DoclingLaunchOutcome.Failed;
        }

        return result;
    }

    private bool Start(DoclingRegistration registration)
    {
        var started = false;
        if (Path.IsPathRooted(registration.Command) && !mProbe.FileExists(registration.Command))
        {
            // Never silently fall back to PATH — a registration that points at a moved or
            // deleted install is a fact the user needs told, not something to paper over.
            mLogger?.LogWarning(MissingCommandMessage, registration.Command);
        }
        else
        {
            try
            {
                Remember(mProcessStarter.Start(BuildStartInfo(registration)));
                started = true;
            }
            catch(Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                          or IOException or UnauthorizedAccessException)
            {
                mLogger?.LogWarning(ex, StartFailedMessage, registration.Command);
            }
        }

        return started;
    }

    private void Remember(IDisposable? launched)
    {
        IDisposable? previous;
        lock(mLaunchedSync)
        {
            previous = mLaunched;
            mLaunched = launched;
        }

        // A relaunch happens only once the previous process has exited, so this releases its handle
        // and nothing else.
        previous?.Dispose();
    }

    private static bool IsRunning(IDisposable launched) => launched switch
                                                            {
                                                                Process process => HasNotExited(process),
                                                                ILaunchedProcess handle => !handle.HasExited,

                                                                // No way to tell: treat it as running so the guard holds.
                                                                _ => true
                                                            };

    private static bool HasNotExited(Process process)
    {
        bool result;
        try
        {
            result = !process.HasExited;
        }
        catch(InvalidOperationException)
        {
            // The handle is not tied to a process any more, so nothing of ours is running.
            result = false;
        }
        catch(System.ComponentModel.Win32Exception)
        {
            // Windows would not say. Count it as running so the guard holds.
            result = true;
        }

        return result;
    }

    private ProcessStartInfo BuildStartInfo(DoclingRegistration registration)
    {
        ProcessStartInfo startInfo = new()
                                     {
                                         FileName = registration.Command,
                                         Arguments = registration.Arguments,
                                         WorkingDirectory = registration.WorkingDirectory,
                                         UseShellExecute = false,
                                         CreateNoWindow = true,
                                         RedirectStandardOutput = true,
                                         RedirectStandardError = true
                                     };
        foreach(KeyValuePair<string, string> entry in registration.Environment)
            startInfo.Environment[entry.Key] = entry.Value;

        ApplyTesseract(startInfo);
        return startInfo;
    }

    private void ApplyTesseract(ProcessStartInfo startInfo)
    {
        TesseractRegistration? tesseract = mRegistry.Read().Tesseract;
        if (tesseract != null)
        {
            if (!string.IsNullOrWhiteSpace(tesseract.TessdataDirectory))
            {
                // Tesseract requires the trailing separator on TESSDATA_PREFIX. Set on the
                // child only — never machine-wide.
                startInfo.Environment[TessdataPrefixVariable] =
                    tesseract.TessdataDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            }

            if (!string.IsNullOrWhiteSpace(tesseract.ExecutableDirectory))
            {
                string existing = startInfo.Environment.TryGetValue(PathVariable, out string? current)
                                      ? current ?? string.Empty
                                      : string.Empty;
                startInfo.Environment[PathVariable] =
                    $"{tesseract.ExecutableDirectory}{Path.PathSeparator}{existing}";
            }
        }
    }

    private async Task<DoclingLaunchOutcome> WaitForReadyAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = mTimeProvider.GetUtcNow() + mSettings.ReadinessTimeout;
        var ready = false;
        while (!ready && mTimeProvider.GetUtcNow() < deadline)
        {
            await Task.Delay(mSettings.PollInterval, cancellationToken);
            ready = await IsHealthyAsync(cancellationToken);
        }

        return ready ? DoclingLaunchOutcome.Ready : DoclingLaunchOutcome.Timeout;
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        var healthy = false;
        try
        {
            using HttpResponseMessage response =
                await mHttpClient.GetAsync(new Uri(mSettings.Endpoint, HealthPath), cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                healthy = HealthResponse.ReportsOk(body);
            }
        }
        catch(Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                     && !cancellationToken.IsCancellationRequested)
        {
            // A quiet endpoint is the ordinary not-started-yet case, not a failure to report.
            healthy = false;
        }

        return healthy;
    }

    private const string HealthPath = "/health";
    private const string TessdataPrefixVariable = "TESSDATA_PREFIX";
    private const string PathVariable = "PATH";
    private const string NotRegisteredMessage =
        "No Docling command is registered; not starting anything. Register one from the SaddleRAG tray.";
    private const string AlreadyAttemptedMessage =
        "Skipping Docling launch -- this session already attempted one; waiting on the existing attempt";
    private const string MissingCommandMessage =
        "The registered Docling command {Command} does not exist; not starting anything";
    private const string StartFailedMessage = "Starting the registered Docling command {Command} failed";
}
