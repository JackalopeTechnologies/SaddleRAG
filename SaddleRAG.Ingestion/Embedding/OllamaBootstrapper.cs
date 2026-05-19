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

    // 15-second probe timeout: a healthy Ollama responds in well under a
    // second, but a busy one (mid model-load, large response in flight)
    // can stall for several seconds. The previous 1-second cap was too
    // tight: bootstrap probes would falsely report unreachable, the
    // bootstrapper would spawn a second `ollama serve` to try to fix it,
    // and that duplicate would spin forever logging "bind: address already
    // in use" while the original was actually fine. Generous probe timeout
    // + reduced attempt count keeps the bootstrap wall clock similar
    // (~45s worst case) while eliminating the false-negative path.
    private static readonly HttpClient smClient = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds: 15) };

    // Set to true the first time LaunchOllamaAsync runs in this process.
    // Subsequent calls bail out immediately so a probe that briefly fails
    // partway through bootstrap can't trigger a second `ollama serve` and
    // leave a duplicate process spamming the log. Process-wide because
    // OllamaBootstrapper is registered as a singleton.
    private static int psLaunchAttempted;

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
    internal static async Task<bool> WaitForReachableAsync(
        Func<CancellationToken, Task<bool>> probe,
        int maxAttempts,
        int delayMs,
        CancellationToken ct,
        Func<int, CancellationToken, Task>? delayFactory = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "maxAttempts must be >= 1");
        if (delayMs < 0)
            throw new ArgumentOutOfRangeException(nameof(delayMs), delayMs, "delayMs must be >= 0");

        var delay = delayFactory ?? ((ms, t) => Task.Delay(ms, t));
        var reachable = false;

        for(var attempt = 0; attempt < maxAttempts && !reachable; attempt++)
        {
            reachable = await probe(ct);
            if (!reachable && attempt < maxAttempts - 1)
                await delay(delayMs, ct);
        }

        return reachable;
    }

    /// <summary>
    ///     Pre-load the configured classification model into Ollama VRAM
    ///     with <c>keep_alive=-1</c> so the first user request doesn't
    ///     pay a multi-second cold-load penalty. Sends a classifier-priming
    ///     prompt and verifies the model returns a "READY" acknowledgement
    ///     so we have evidence the model actually loaded and can serve --
    ///     a 200 response with empty body is a degenerate "warm" that
    ///     would still leave the first real classify call cold-loading.
    ///     Throws on timeout, HTTP error, or missing/unrecognised response
    ///     so the warmup host can mark the bootstrap as failed instead of
    ///     silently flipping the MCP healthy when nothing is actually ready.
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
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            var endpoint = new Uri(new Uri(mSettings.Endpoint), GenerateEndpointPath);

            foreach(string model in distinct)
                await WarmSingleModelAsync(client, endpoint, model, timeoutSeconds, ct);
        }
    }

    /// <summary>
    ///     Send the classifier-priming warm prompt for one model and verify
    ///     the model replied with the expected READY acknowledgement.
    ///     Exposed <c>internal</c> so tests can drive the HTTP path with a
    ///     stubbed <see cref="HttpClient" /> without spinning up Ollama.
    /// </summary>
    internal async Task WarmSingleModelAsync(HttpClient client,
                                             Uri endpoint,
                                             string model,
                                             int timeoutSeconds,
                                             CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(model);

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
            var body = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();

            if (!ResponseContainsExpectedToken(body))
            {
                mLogger.LogError("Warm of {Model} returned HTTP 200 but response did not contain '{Token}'. Body: {Body}",
                                 model,
                                 ExpectedWarmupToken,
                                 Truncate(body, maxLength: WarmupBodyLogCap));
                throw new
                    InvalidOperationException($"Ollama warm of '{model}' returned an unexpected body. Expected response containing '{ExpectedWarmupToken}'."
                                             );
            }

            mLogger.LogInformation("Warmed {Model} in {Ms}ms (response acknowledged)", model, sw.ElapsedMilliseconds);
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested)
        {
            sw.Stop();
            mLogger.LogError("Timed out warming {Model} after {Ms}ms (timeout {TimeoutSeconds}s)",
                             model,
                             sw.ElapsedMilliseconds,
                             timeoutSeconds
                            );
            throw new
                TimeoutException($"Ollama warm of '{model}' did not complete within {timeoutSeconds}s. Verify only one ollama serve process is running and the model is fully downloaded."
                                );
        }
    }

    private static bool ResponseContainsExpectedToken(string body)
    {
        bool res = false;
        if (!string.IsNullOrEmpty(body))
            res = body.Contains(ExpectedWarmupToken, StringComparison.OrdinalIgnoreCase);
        return res;
    }

    private static string Truncate(string value, int maxLength)
    {
        string res = value;
        if (!string.IsNullOrEmpty(value) && value.Length > maxLength)
            res = value[..maxLength] + WarmupBodyTruncatedSuffix;
        return res;
    }

    private const string GenerateEndpointPath = "/api/generate";
    // Classifier-priming warm prompt. Two jobs:
    //   1. Force Ollama to actually load + activate the model (a request
    //      with prompt="." has occasionally been observed to skip token
    //      generation entirely on some Ollama builds, returning HTTP 200
    //      against a model that never finished loading -- the warm
    //      contract is "model is ready to classify on first real call",
    //      so we need observable token output).
    //   2. Prime the model on its actual job so the warm pass doubles as
    //      a sanity check that the model loaded into a usable state.
    // The expected reply is a single "READY" token; the validator checks
    // case-insensitively so minor capitalization drift doesn't fail warm.
    private const string WarmupPrompt =
        "You are about to be used as a documentation classifier. You will receive documentation pages and assign each to one of these categories: Overview, HowTo, Sample, Code, ApiReference, ChangeLog, Unclassified. To confirm you have loaded and understood, reply with exactly the single word: READY";
    private const string ExpectedWarmupToken = "READY";
    private const int WarmupBodyLogCap = 256;
    private const string WarmupBodyTruncatedSuffix = "...";
    private const int KeepAliveForever = -1;
    private const int MinimumWarmModelTimeoutSeconds = 1;

    private const string OllamaWindowsInstallerUrl = "https://ollama.com/download/OllamaSetup.exe";
    internal static string OllamaExeName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OllamaExeNameWindows : OllamaExeNamePosix;
    private const int MaxStartWaitSeconds = 30;
    private const int PostInstallDelayMs = 3000;
    private const int ServicePollDelayMs = 1000;
    private const int ProgressLogInterval = 10;
    // Bootstrap-time reachability probe: 3 attempts × 15s probe timeout
    // + 1s delay = ~48s worst-case wall clock when Ollama is genuinely
    // unreachable. Healthy responses come back in well under 1s so the
    // normal case is still near-instant. The longer per-probe timeout
    // (vs. the previous 1s) eliminates the false-negative that triggered
    // a duplicate `ollama serve` launch when the existing Ollama was
    // momentarily slow.
    private const int BootstrapReachabilityMaxAttempts = 3;
    private const int BootstrapReachabilityPollMs = 1000;
    // Post-launch poll: tighter per-call timeout (2s) because the freshly-
    // spawned `ollama serve` should bind its socket in well under a second.
    // If 2s isn't enough, something is wrong with the launch itself and
    // longer per-attempt waiting won't help.
    private const int PostLaunchProbeTimeoutSeconds = 2;

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

    private static string? FindOllamaExecutable() =>
        FindOllamaExecutable(Environment.GetEnvironmentVariable(PathEnvironmentVariable),
                             File.Exists,
                             OllamaExeName,
                             GetDefaultCommonPaths()
                            );

    /// <summary>
    ///     Search a PATH-style string and a fallback list of common install
    ///     paths for <paramref name="exeName" />, returning the first hit
    ///     according to <paramref name="fileExists" />. Pure: no real
    ///     environment or file-system access, so tests can drive the search
    ///     deterministically.
    /// </summary>
    internal static string? FindOllamaExecutable(string? pathEnvValue,
                                                 Func<string, bool> fileExists,
                                                 string exeName,
                                                 IReadOnlyList<string> commonPaths)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentException.ThrowIfNullOrEmpty(exeName);
        ArgumentNullException.ThrowIfNull(commonPaths);

        string? result = null;

        var pathDirs = pathEnvValue?.Split(Path.PathSeparator) ?? [];
        foreach(var dir in pathDirs.Where(d => result == null && !string.IsNullOrEmpty(d)))
        {
            var candidate = Path.Combine(dir, exeName);
            if (fileExists(candidate))
                result = candidate;
        }

        foreach(var path in commonPaths.Where(_ => result == null))
        {
            if (fileExists(path))
                result = path;
        }

        return result;
    }

    private static IReadOnlyList<string> GetDefaultCommonPaths()
    {
        IReadOnlyList<string> result = OperatingSystem.IsWindows()
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

            // Brief pause to let the installer's background processes (registry
            // writes, PATH updates) get started before we probe. We are NOT
            // relying on this delay to guarantee completion — FindOllamaExecutable()
            // is called immediately after and throws if the executable still
            // isn't present.
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

    /// <summary>
    ///     Internal test seam: when set, <see cref="LaunchOllamaInternalAsync" />
    ///     calls this delegate instead of <see cref="Process.Start(ProcessStartInfo)" />.
    ///     The single-shot-guard test uses this to verify the guard prevents
    ///     a second spawn without actually launching a real `ollama serve`.
    ///     Tests reset to null in a finally block.
    /// </summary>
    internal static Func<ProcessStartInfo, Process?>? pmProcessStarterOverride;

    /// <summary>
    ///     Reset the process-wide single-shot launch flag. Tests call this
    ///     between cases to exercise the guard repeatedly; never invoked in
    ///     production code.
    /// </summary>
    internal static void ResetLaunchGuardForTesting() => Interlocked.Exchange(ref psLaunchAttempted, value: 0);

    private async Task LaunchOllamaAsync(CancellationToken ct)
    {
        // Atomic single-shot guard: prevents a second `ollama serve` spawn
        // when the bootstrap probe transiently fails part-way through
        // startup. Interlocked.Exchange returns the previous value, so a
        // non-zero result means another caller already won the race and
        // launched (or is about to launch) the process. The original wedge
        // we saw was multiple `ollama serve` processes spamming "bind:
        // address already in use" while the legitimate listener was hung
        // on its own model-load -- this is the structural fix.
        bool alreadyAttempted = Interlocked.Exchange(ref psLaunchAttempted, value: 1) != 0;
        if (alreadyAttempted)
            mLogger.LogInformation("Skipping Ollama launch -- this process already attempted one this session");
        else
            await LaunchOllamaInternalAsync(ct);
    }

    private async Task LaunchOllamaInternalAsync(CancellationToken ct)
    {
        mLogger.LogInformation("Ollama not reachable, attempting to start...");

        string ollamaPath = FindOllamaExecutable() ?? OllamaCommandName;

        try
        {
            var startInfo = new ProcessStartInfo
                                {
                                    FileName = ollamaPath,
                                    Arguments = "serve",
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true
                                };
            var started = pmProcessStarterOverride != null
                              ? pmProcessStarterOverride(startInfo)
                              : Process.Start(startInfo);

            if (started != null)
                mLogger.LogInformation("Spawned ollama serve (PID {Pid})", started.Id);

            var bound = false;
            for(var i = 0; i < MaxStartWaitSeconds && !bound; i++)
            {
                await Task.Delay(ServicePollDelayMs, ct);
                bound = await IsReachableAsync(PostLaunchProbeTimeoutSeconds, ct);
                if (bound)
                    mLogger.LogInformation("Ollama started successfully (bound after {Seconds}s)", i + 1);
            }

            if (!bound)
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

    /// <summary>
    ///     Probe the configured endpoint with an explicit per-call timeout
    ///     in seconds. Used by the post-launch poll which wants tighter
    ///     per-attempt budgets than the 15s default on smClient.
    /// </summary>
    private async Task<bool> IsReachableAsync(int timeoutSeconds, CancellationToken ct) =>
        await IsReachableAsync(smClient, new Uri(mSettings.Endpoint), timeoutSeconds, ct);

    /// <summary>
    ///     Pure-static probe helper: GET <paramref name="endpoint" /> through
    ///     <paramref name="client" /> with a per-call timeout, returning true
    ///     on HTTP success and false on timeout, transport error, or any
    ///     other exception. Exposed <c>internal static</c> so tests can drive
    ///     the probe with a stubbed <see cref="HttpClient" /> without
    ///     hard-coding network access. External cancellation propagates
    ///     untouched (the catch only suppresses transport/timeout errors).
    /// </summary>
    internal static async Task<bool> IsReachableAsync(HttpClient client,
                                                      Uri endpoint,
                                                      int timeoutSeconds,
                                                      CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (timeoutSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds),
                                                  timeoutSeconds,
                                                  "timeoutSeconds must be >= 1");

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        bool result;
        try
        {
            var response = await client.GetAsync(endpoint, probeCts.Token);
            result = response.IsSuccessStatusCode;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            result = false;
        }

        return result;
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

        var requiredModels = ResolveRequiredModels(mSettings, additionalModels);

        var localModels = await client.ListLocalModelsAsync(ct);
        var availableNames = localModels
                             .Select(m => m.Name)
                             .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach(string model in requiredModels)
        {
            if (IsModelAvailable(model, availableNames))
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
    ///     legacy reranker model nobody will use). <c>internal static</c>
    ///     so tests can drive the resolution against a settings object
    ///     without standing up the rest of the bootstrapper.
    /// </summary>
    internal static HashSet<string> ResolveRequiredModels(OllamaSettings settings,
                                                          IReadOnlyList<string>? additionalModels)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                           {
                               settings.EmbeddingModel
                           };

        var classificationModelName = settings.GetActiveClassificationModel().Name;
        if (!string.IsNullOrEmpty(classificationModelName))
            required.Add(classificationModelName);

        if (additionalModels != null)
        {
            foreach(string model in additionalModels.Where(m => !string.IsNullOrEmpty(m)))
                required.Add(model);
        }

        return required;
    }

    /// <summary>
    ///     Determine whether <paramref name="model" /> is satisfied by any
    ///     name in <paramref name="availableNames" />. Matches three ways:
    ///     exact (case-insensitive), <c>{model}:latest</c>, or
    ///     <c>{model}*</c> prefix. The prefix match handles tag-specific
    ///     installs like <c>phi4:14b</c> satisfying a request for
    ///     <c>phi4</c>.
    /// </summary>
    internal static bool IsModelAvailable(string model, IReadOnlySet<string> availableNames)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        ArgumentNullException.ThrowIfNull(availableNames);

        var result = availableNames.Contains(model) ||
                     availableNames.Contains($"{model}:latest") ||
                     availableNames.Any(n => n.StartsWith(model, StringComparison.OrdinalIgnoreCase));
        return result;
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
