// // OllamaBootstrapper.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;

#endregion

namespace DocRAG.Ingestion.Embedding;

/// <summary>
///     Ensures Ollama is installed, running, and required models are available.
///     Fully self-bootstrapping â€” the only prerequisite is MongoDB with scraped data.
/// </summary>
public class OllamaBootstrapper
{
    public OllamaBootstrapper(IOptions<OllamaSettings> settings,
                              ILogger<OllamaBootstrapper> logger)
    {
        mSettings = settings.Value;
        mLogger = logger;
    }

    private readonly ILogger<OllamaBootstrapper> mLogger;

    private readonly OllamaSettings mSettings;

    /// <summary>
    ///     Full bootstrap sequence: install â†’ start â†’ pull models.
    /// </summary>
    public async Task BootstrapAsync(IReadOnlyList<string>? additionalModels = null,
                                     CancellationToken ct = default)
    {
        await EnsureInstalledAsync(ct);
        await EnsureRunningAsync(ct);
        await EnsureModelsAsync(additionalModels, ct);

        mLogger.LogInformation("Ollama bootstrap complete");
    }

    private const string OllamaWindowsInstallerUrl = "https://ollama.com/download/OllamaSetup.exe";
    private const string OllamaExeName = "ollama.exe";
    private const int MaxStartWaitSeconds = 30;
    private const int MaxInstallWaitSeconds = 120;
    private const int PostInstallDelayMs = 3000;
    private const int ServicePollDelayMs = 1000;
    private const int ProgressLogInterval = 10;

    private const string InstallOnlyWindowsMessage = "Automatic Ollama installation is only supported on Windows. ";
    private const string InstallManuallyMessage = "Install Ollama manually from https://ollama.com";
    private const string InstallCompletedNotFoundMessage = "Ollama installation completed but executable not found. ";
    private const string TryInstallingManuallyMessage = "Try installing manually from https://ollama.com";
    private const string PathEnvironmentVariable = "PATH";
    private const string ProgramsFolderName = "Programs";
    private const string OllamaFolderName = "Ollama";
    private const string TempInstallFolderName = "DocRAG_OllamaInstall";
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

        string[] pathDirs = Environment.GetEnvironmentVariable(PathEnvironmentVariable)?.Split(Path.PathSeparator) ?? [];
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
            string[] commonPaths =
                [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                 ProgramsFolderName,
                                 OllamaFolderName,
                                 OllamaExeName
                                ),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                 OllamaFolderName,
                                 OllamaExeName
                                ),
                    @"C:\Program Files\Ollama\ollama.exe"
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
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(minutes: 5) };
            var response = await httpClient.GetAsync(OllamaWindowsInstallerUrl, ct);
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
        string machinePath = Environment.GetEnvironmentVariable(PathEnvironmentVariable, EnvironmentVariableTarget.Machine) ??
                             string.Empty;
        string userPath = Environment.GetEnvironmentVariable(PathEnvironmentVariable, EnvironmentVariableTarget.User) ?? string.Empty;
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
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds: 5) };
            var response = await client.GetAsync(mSettings.Endpoint, ct);
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

        var requiredModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                 {
                                     mSettings.EmbeddingModel
                                 };

        if (!string.IsNullOrEmpty(mSettings.ClassificationModel))
            requiredModels.Add(mSettings.ClassificationModel);

        if (!string.IsNullOrEmpty(mSettings.ReRankingModel) && mSettings.ReRankingEnabled)
            requiredModels.Add(mSettings.ReRankingModel);

        if (additionalModels != null)
        {
            foreach(string model in additionalModels)
                requiredModels.Add(model);
        }

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
                mLogger.LogInformation("Pulling model {Model} â€” this may take several minutes on first run...", model);
                await PullModelAsync(client, model, ct);
            }
        }
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
