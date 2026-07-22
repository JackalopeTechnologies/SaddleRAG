// PlaywrightRuntimeProbe.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License in the repo root.

#region Usings

using System.Diagnostics;
using Microsoft.Playwright;

#endregion

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Startup provisioner for the browser runtime that backs <see cref="PageCrawler" />.
/// </summary>
public sealed class PlaywrightRuntimeProbe : IPlaywrightRuntimeProbe
{
    /// <inheritdoc />
    public async Task VerifyAsync(CancellationToken ct = default)
    {
        try
        {
            await VerifyBrowserLaunchAsync(ct);
        }
        catch(PlaywrightException exception)
        {
            if (!IsBrowserMissing(exception))
                throw;

            await InstallChromiumAsync(ct);
            await VerifyBrowserLaunchAsync(ct);
        }
    }

    internal static bool IsBrowserMissing(PlaywrightException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        bool result = exception.Message.Contains(BrowserExecutableMissingMessage,
                                                 StringComparison.OrdinalIgnoreCase
                                                );
        return result;
    }

    private static async Task VerifyBrowserLaunchAsync(CancellationToken ct)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        ct.ThrowIfCancellationRequested();
    }

    private static async Task InstallChromiumAsync(CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
                            {
                                FileName = PowerShellExecutable,
                                UseShellExecute = false,
                                RedirectStandardError = true,
                                RedirectStandardOutput = true
                            };
        startInfo.ArgumentList.Add(NoProfileArgument);
        startInfo.ArgumentList.Add(ExecutionPolicyArgument);
        startInfo.ArgumentList.Add(BypassArgument);
        startInfo.ArgumentList.Add(FileArgument);
        startInfo.ArgumentList.Add(InstallScriptPath);
        startInfo.ArgumentList.Add(InstallArgument);
        startInfo.ArgumentList.Add(ChromiumArgument);

        using Process process = Process.Start(startInfo) ??
                                throw new InvalidOperationException($"Could not start {PowerShellExecutable} to install Playwright Chromium.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> standardError = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        string output = await standardOutput;
        string error = await standardError;

        if (process.ExitCode != ProcessSuccessExitCode)
        {
            throw new InvalidOperationException($"Playwright Chromium installation failed (exit code {process.ExitCode}). " +
                                                $"Run '{InstallScriptPath} install chromium' manually. {output} {error}".Trim()
                                               );
        }
    }

    private static string InstallScriptPath => Path.Combine(AppContext.BaseDirectory, InstallScriptFileName);

    private const string BrowserExecutableMissingMessage = "Executable doesn't exist";
    private static string PowerShellExecutable => GetPowerShellExecutable(OperatingSystem.IsWindows());

    internal static string GetPowerShellExecutable(bool isWindows)
    {
        string result = isWindows ? WindowsPowerShellExecutable : PowerShellCoreExecutable;
        return result;
    }

    private const string WindowsPowerShellExecutable = "powershell.exe";
    private const string PowerShellCoreExecutable = "pwsh";
    private const string NoProfileArgument = "-NoProfile";
    private const string ExecutionPolicyArgument = "-ExecutionPolicy";
    private const string BypassArgument = "Bypass";
    private const string FileArgument = "-File";
    private const string InstallArgument = "install";
    private const string ChromiumArgument = "chromium";
    private const string InstallScriptFileName = "playwright.ps1";
    private const int ProcessSuccessExitCode = 0;
}