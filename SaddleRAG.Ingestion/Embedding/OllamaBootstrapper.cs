// OllamaBootstrapper.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Ensures Ollama is installed, running, and required models are available.
///     Fully self-bootstrapping — the only prerequisite is MongoDB with scraped data.
/// </summary>
public class OllamaBootstrapper
{
    private static readonly HttpClient smHttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(minutes: 5) };

    // 1-second probe timeout: short enough that 30 retries finish in well
    // under a minute when Ollama just isn't there (Windows fresh install),
    // long enough to clear the latency budget of a healthy reachable
    // sidecar response.
    private static readonly HttpClient smClient = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds: 1) };

    public OllamaBootstrapper(IOptions<OllamaSettings> settings,
                              ILogger<OllamaBootstrapper> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        mSettings = settings.Value;
        mLogger = logger;
    }

    private readonly ILogger<OllamaBootstrapper> mLogger;
    private readonly OllamaSettings mSettings;

    /// <summary>
    ///     Full bootstrap sequence: install → start → pull models.
    ///     Probes <see cref="OllamaSettings.Endpoint" /> with a bounded
    ///     retry loop before falling through to install. Docker sidecar
    ///     deployments need the patience — `depends_on` only waits for the
    ///     ollama container to be created, not for the network listener to
    ///     come up, so the first probe can race against ollama's startup
    ///     and return false on a healthy system. Without the retry, the
    ///     SaddleRAG container would fall through to <see cref="EnsureInstalledAsync" />
    ///     and throw <see cref="PlatformNotSupportedException" /> on Linux.
    /// </summary>
    public async Task BootstrapAsync(IReadOnlyList<string>? additionalModels = null,
                                     CancellationToken ct = default)
    {
        var reachable = await WaitForReachableAsync(IsReachableAsync,
                                                    BootstrapReachabilityMaxAttempts,
                                                    BootstrapReachabilityPollMs,
                                                    ct
                                                   );

        if (reachable)
            mLogger.LogInformation("Ollama reachable at {Endpoint}, skipping install/start", mSettings.Endpoint);
        else
        {
            await EnsureInstalledAsync(ct);
            await EnsureRunningAsync(ct);
        }

        await EnsureModelsAsync(additionalModels, ct);
        mLogger.LogInformation("Ollama bootstrap complete");
    }

    /// <summary>
    ///     Probe <paramref name="probe" /> until it returns true or
    ///     <paramref name="maxAttempts" /> is exhausted, sleeping
    ///     <paramref name="delayMs" /> between attempts. Returns true on
    ///     the first successful probe; returns false only after every
    ///     attempt has failed. Exposed internally so tests can drive the
    ///     retry loop with a stub probe instead of an HTTP server.
    /// </summary>
    internal static async Task<bool> WaitForReachableAsync(Func<CancellationToken, Task<bool>> probe,
                                                           int maxAttempts,
                                                           int delayMs,
                                                           CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "maxAttempts must be >= 1");
        if (delayMs < 0)
            throw new ArgumentOutOfRangeException(nameof(delayMs), delayMs, "delayMs must be >= 0");

        var reachable = false;

        for(var attempt = 0; attempt < maxAttempts && !reachable; attempt++)
        {
            reachable = await probe(ct);
            if (!reachable && attempt < maxAttempts - 1)
                await Task.Delay(delayMs, ct);
        }

        return reachable;
    }

    /// <summary>
    ///     Pre-load the configured generate-capable models (classification +
    ///     reranking) into Ollama VRAM so the first user request doesn't
    ///     pay a multi-second cold-load penalty. Uses keep_alive=-1 so the
    ///     models stay resident across idle gaps.
    /// </summary>
    public async Task WarmModelsAsync(CancellationToken ct = default)
    {
        var candidates = new[]
                             {
                                 mSettings.GetActiveClassificationModel().Name
                             };
        var distinct = candidates
                       .Where(m => !string.IsNullOrWhiteSpace(m))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();

        var timeoutSeconds = mSettings.WarmModelTimeoutSeconds;
        if (timeoutSeconds < MinimumWarmModelTimeoutSeconds)
            timeoutSeconds = OllamaSettings.DefaultWarmModelTimeoutSeconds;

        if (distinct.Count > 0)
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            var endpoint = new Uri(new Uri(mSettings.Endpoint), GenerateEndpointPath);

            foreach(string model in distinct)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var payload = new
                                      {
                                          model,
                                          prompt = WarmupPrompt,
                                          stream = false,
                                          keep_alive = KeepAliveForever
                                      };
                    var response = await client.PostAsJsonAsync(endpoint, payload, ct);
                    response.EnsureSuccessStatusCode();
                    sw.Stop();
                    mLogger.LogInformation("Warmed {Model} in {Ms}ms", model, sw.ElapsedMilliseconds);
                }
                catch(OperationCanceledException ex) when(!ct.IsCancellationRequested)
                {
                    sw.Stop();
                    mLogger.LogWarning(ex,
                                       "Timed out warming {Model} after {Ms}ms (timeout {TimeoutSeconds}s)",
                                       model,
                                       sw.ElapsedMilliseconds,
                                       timeoutSeconds
                                      );
                }
                catch(Exception ex) when(ex is not OperationCanceledException)
                {
                    sw.Stop();
                    mLogger.LogWarning(ex, "Failed to warm {Model} after {Ms}ms", model, sw.ElapsedMilliseconds);
                }
            }
        }
    }

    private const string GenerateEndpointPath = "/api/generate";
    private const string WarmupPrompt = ".";
    private const int KeepAliveForever = -1;
    private const int MinimumWarmModelTimeoutSeconds = 1;

    private const string OllamaWindowsInstallerUrl = "https://ollama.com/download/OllamaSetup.exe";
    internal static string OllamaExeName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OllamaExeNameWindows : OllamaExeNamePosix;
    private const int MaxStartWaitSeconds = 30;
    private const int PostInstallDelayMs = 3000;
    private const int ServicePollDelayMs = 1000;
    private const int ProgressLogInterval = 10;
    // Bootstrap-time reachability probe: 30 attempts × 1s = ~30s wall
    // clock with smClient's 1s HTTP timeout absorbing connection-refused
    // and DNS-fail fast. Tuned for "Docker sidecar warming up" without
    // blowing the budget for "Windows fresh install with no Ollama yet."
    private const int BootstrapReachabilityMaxAttempts = 30;
    private const int BootstrapReachabilityPollMs = 1000;

    private const string OllamaExeNameWindows = "ollama.exe";
    private const string OllamaExeNamePosix = "ollama";
    private const string OllamaPathLocalBin = "/usr/local/bin/ollama";
    private const string OllamaPathUsrBin = "/usr/bin/ollama";
    private const string OllamaDotDir = ".ollama";
    private const string OllamaBinDir = "bin";
    private const string InstallOnlyWindowsMessage = "Automatic Ollama installation is only supported on Windows. ";
    private const string InstallManuallyMessage = "Install Ollama manually from https://ollama.com";
    private const string InstallCompletedNotFoundMessage = "Ollama installation completed but executable not found. ";
    private const string TryInstallingManuallyMessage = "Try installing manually from https://ollama.com";
    private const string PathEnvironmentVariable = "PATH";
    private const string ProgramsFolderName = "Programs";
    private const string OllamaFolderName = "Ollama";
    private const string TempInstallFolderName = "SaddleRAG_OllamaInstall";
    private const string InstallerFileName = "OllamaSetup.exe";
    private const string OllamaCommandName = "ollama";

    #region Installation

    private async Task EnsureInstalledAsync(CancellationToken ct)
    {
        string? ollamaPath = FindOllamaExecutable();

        if (ollamaPath != null)
            mLogger.LogInformation("Ollama found at {Path}", ollamaPath);
        else
        {
            mLogger.LogInformation("Ollama not found, installing...");

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException(InstallOnlyWindowsMessage +
                                                        InstallManuallyMessage
                                                       );
            }

            await DownloadAndInstallWindowsAsync(ct);

            ollamaPath = FindOllamaExecutable();
            if (ollamaPath == null)
            {
                throw new InvalidOperationException(InstallCompletedNotFoundMessage +
                                                    TryInstallingManuallyMessage
                                                   );
            }

            mLogger.LogInformation("Ollama installed successfully at {Path}", ollamaPath);
        }
    }

    private static string? FindOllamaExecutable()
    {
        string? result = null;

        string[] pathDirs = Environment.GetEnvironmentVariable(PathEnvironmentVariable)?.Split(Path.PathSeparator) ??
                                [];
        foreach(string dir in pathDirs)
        {
            if (result == null)
            {
                string candidate = Path.Combine(dir, OllamaExeName);
                if (File.Exists(candidate))
                    result = candidate;
            }
        }

        if (result == null)
        {
            string[] commonPaths = OperatingSystem.IsWindows()
                ? [
                      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                   ProgramsFolderName,
                                   OllamaFolderName,
                                   OllamaExeName),
                      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                   OllamaFolderName,
                                   OllamaExeName),
                      @"C:\Program Files\Ollama\ollama.exe"
                  ]
                : [
                      OllamaPathLocalBin,
                      OllamaPathUsrBin,
                      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                   OllamaDotDir, OllamaBinDir, OllamaExeNamePosix)
                  ];

            foreach(string path in commonPaths)
            {
                if (result == null && File.Exists(path))
                    result = path;
            }
        }

        return result;
    }

    private async Task DownloadAndInstallWindowsAsync(CancellationToken ct)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), TempInstallFolderName);
        Directory.CreateDirectory(tempDir);
        string installerPath = Path.Combine(tempDir, InstallerFileName);

        try
        {
            mLogger.LogInformation("Downloading Ollama installer...");
            var response = await smHttpClient.GetAsync(OllamaWindowsInstallerUrl, ct);
            response.EnsureSuccessStatusCode();

            await using var fileStream = File.Create(installerPath);
            await response.Content.CopyToAsync(fileStream, ct);
            await fileStream.FlushAsync(ct);
            fileStream.Close();

            mLogger.LogInformation("Running Ollama installer (silent)...");

            var process = Process.Start(new ProcessStartInfo
                                            {
                                                FileName = installerPath,
                                                Arguments = "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES",
                                                UseShellExecute = false,
                                                CreateNoWindow = true
                                            }
                                       );

            if (process != null)
            {
                await process.WaitForExitAsync(ct);

                if (process.ExitCode != 0)
                    mLogger.LogWarning("Ollama installer exited with code {Code}", process.ExitCode);
            }

            await Task.Delay(PostInstallDelayMs, ct);

            RefreshPathEnvironment();
        }
        finally
        {
            try
            {
                if (File.Exists(installerPath))
                    File.Delete(installerPath);
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    private static void RefreshPathEnvironment()
    {
        string machinePath =
            Environment.GetEnvironmentVariable(PathEnvironmentVariable, EnvironmentVariableTarget.Machine) ??
            string.Empty;
        string userPath = Environment.GetEnvironmentVariable(PathEnvironmentVariable, EnvironmentVariableTarget.User) ??
                          string.Empty;
        Environment.SetEnvironmentVariable(PathEnvironmentVariable, $"{machinePath};{userPath}");
    }

    #endregion

    #region Service management

    private async Task EnsureRunningAsync(CancellationToken ct)
    {
        bool alreadyReachable = await IsReachableAsync(ct);

        if (alreadyReachable)
            mLogger.LogInformation("Ollama is running at {Endpoint}", mSettings.Endpoint);
        else
            await LaunchOllamaAsync(ct);
    }

    private async Task LaunchOllamaAsync(CancellationToken ct)
    {
        mLogger.LogInformation("Ollama not reachable, attempting to start...");

        string ollamaPath = FindOllamaExecutable() ?? OllamaCommandName;

        try
        {
            Process.Start(new ProcessStartInfo
                              {
                                  FileName = ollamaPath,
                                  Arguments = "serve",
                                  UseShellExecute = false,
                                  CreateNoWindow = true,
                                  RedirectStandardOutput = true,
                                  RedirectStandardError = true
                              }
                         );

            var started = false;
            for(var i = 0; i < MaxStartWaitSeconds; i++)
            {
                await Task.Delay(ServicePollDelayMs, ct);
                if (!started && await IsReachableAsync(ct))
                {
                    mLogger.LogInformation("Ollama started successfully");
                    started = true;
                }
            }

            if (!started)
            {
                throw new
                    TimeoutException($"Ollama started but not reachable after {MaxStartWaitSeconds}s at {mSettings.Endpoint}"
                                    );
            }
        }
        catch(Exception ex) when(ex is not TimeoutException)
        {
            mLogger.LogError(ex, "Failed to start Ollama");
            throw new
                InvalidOperationException($"Cannot start Ollama. Verify installation at {FindOllamaExecutable() ?? "unknown path"}.",
                                          ex
                                         );
        }
    }

    private async Task<bool> IsReachableAsync(CancellationToken ct)
    {
        var result = false;
        try
        {
            var response = await smClient.GetAsync(mSettings.Endpoint, ct);
            result = response.IsSuccessStatusCode;
        }
        catch
        {
            // Not reachable
        }

        return result;
    }

    #endregion

    #region Model management

    private async Task EnsureModelsAsync(IReadOnlyList<string>? additionalModels,
                                         CancellationToken ct)
    {
        var client = new OllamaApiClient(new Uri(mSettings.Endpoint));

        var requiredModels = ResolveRequiredModels(additionalModels);

        var localModels = await client.ListLocalModelsAsync(ct);
        var availableNames = localModels
                             .Select(m => m.Name)
                             .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach(string model in requiredModels)
        {
            bool isAvailable = availableNames.Contains(model) ||
                               availableNames.Contains($"{model}:latest") ||
                               availableNames.Any(n => n.StartsWith(model, StringComparison.OrdinalIgnoreCase));

            if (isAvailable)
                mLogger.LogInformation("Model {Model} is available", model);
            else
            {
                mLogger.LogInformation("Pulling model {Model} — this may take several minutes on first run...", model);
                await PullModelAsync(client, model, ct);
            }
        }
    }

    /// <summary>
    ///     Build the set of models to ensure-on-startup. Embedding and
    ///     classification are always required; the reranker model depends
    ///     on the configured ReRankerStrategy so we don't pull a 1.9GB
    ///     cross-encoder when the strategy is Off (or vice versa, pull a
    ///     legacy reranker model nobody will use).
    /// </summary>
    private HashSet<string> ResolveRequiredModels(IReadOnlyList<string>? additionalModels)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                           {
                               mSettings.EmbeddingModel
                           };

        var classificationModelName = mSettings.GetActiveClassificationModel().Name;
        if (!string.IsNullOrEmpty(classificationModelName))
            required.Add(classificationModelName);

        if (additionalModels != null)
        {
            foreach(string model in additionalModels.Where(m => !string.IsNullOrEmpty(m)))
                required.Add(model);
        }

        return required;
    }

    private async Task PullModelAsync(OllamaApiClient client, string model, CancellationToken ct)
    {
        long lastPercent = -1;

        await foreach(var status in client.PullModelAsync(model, ct))
        {
            if (status?.Percent != null && (long) status.Percent != lastPercent)
            {
                lastPercent = (long) status.Percent;
                if (lastPercent % ProgressLogInterval == 0)
                    mLogger.LogInformation("Pulling {Model}: {Percent}%", model, lastPercent);
            }
        }

        mLogger.LogInformation("Model {Model} pulled successfully", model);
    }

    #endregion
}
