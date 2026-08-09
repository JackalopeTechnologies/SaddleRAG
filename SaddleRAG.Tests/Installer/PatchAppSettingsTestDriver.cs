// PatchAppSettingsTestDriver.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace SaddleRAG.Tests.Installer;

/// <summary>Bounded driver for the production atomic settings patch.</summary>
internal static class PatchAppSettingsTestDriver
{
    internal static JsonObject Run(JsonObject settings, string doclingEndpoint)
    {
        ArgumentNullException.ThrowIfNull(settings);
        PatchAppSettingsRun run = RunWithFiles(settings, doclingEndpoint);
        JsonObject result;
        try
        {
            if (run.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"PatchAppSettings.ps1 exited with code {run.ExitCode}. stderr: {run.StandardError}");
            }

            JsonNode? parsed = JsonNode.Parse(File.ReadAllText(run.AppSettingsPath));
            result = parsed as JsonObject
                     ?? throw new InvalidOperationException("The patched settings file was not a JSON object.");
        }
        finally
        {
            File.Delete(run.AppSettingsPath);
            File.Delete(run.AppSettingsPath + TempFileSuffix);
        }

        return result;
    }

    internal static PatchAppSettingsRun RunWithFiles(JsonObject settings,
                                                     string doclingEndpoint)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(doclingEndpoint);
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        string? repositoryRoot = InstallerSourceTreeResolver.TryResolveRepositoryRoot();
        if (repositoryRoot == null)
            Assert.Skip(RepositoryMissingSkipReason);
        Assert.NotNull(repositoryRoot);

        string scriptPath = Path.Combine(repositoryRoot,
                                         InstallerDirectoryName,
                                         PatchScriptFileName);
        string appSettingsPath = Path.Combine(Path.GetTempPath(),
                                              $"saddlerag-appsettings-docling-{Guid.NewGuid():N}.json");
        File.WriteAllText(appSettingsPath, settings.ToJsonString(), Encoding.UTF8);
        ProcessExecutionResult execution = Invoke(scriptPath,
                                                  appSettingsPath,
                                                  doclingEndpoint);
        return new PatchAppSettingsRun(repositoryRoot,
                                       appSettingsPath,
                                       execution.ExitCode,
                                       execution.StandardOutput,
                                       execution.StandardError);
    }

    private static ProcessExecutionResult Invoke(string scriptPath,
                                                 string appSettingsPath,
                                                 string doclingEndpoint)
    {
        var startInfo = new ProcessStartInfo(WindowsPowerShellExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        AddArgument(startInfo, NoProfileArgument);
        AddArgument(startInfo, ExecutionPolicyArgument);
        AddArgument(startInfo, BypassArgument);
        AddArgument(startInfo, FileArgument);
        AddArgument(startInfo, scriptPath);
        AddNamedArgument(startInfo, AppSettingsPathArgument, appSettingsPath);
        AddNamedArgument(startInfo, ConnectionStringArgument, DefaultMongoConnection);
        AddNamedArgument(startInfo, DatabaseNameArgument, DefaultDatabaseName);
        AddNamedArgument(startInfo, OllamaEndpointArgument, DefaultOllamaEndpoint);
        AddNamedArgument(startInfo, DoclingEndpointArgument, doclingEndpoint);
        AddNamedArgument(startInfo, ExecutionProviderArgument, DefaultExecutionProvider);
        AddNamedArgument(startInfo, EscapeFailedArgument, string.Empty);

        using Process? process = Process.Start(startInfo);
        if (process == null)
            throw new InvalidOperationException("Failed to start the settings patch process.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProcessTimeout))
            Assert.Fail($"PatchAppSettings.ps1 did not exit within {ProcessTimeout.TotalSeconds:N0} seconds.");
        standardOutput.Wait();
        standardError.Wait();
        return new ProcessExecutionResult(process.ExitCode,
                                          standardOutput.Result,
                                          standardError.Result);
    }

    private static void AddNamedArgument(ProcessStartInfo startInfo,
                                         string name,
                                         string value)
    {
        AddArgument(startInfo, name);
        AddArgument(startInfo, value);
    }

    private static void AddArgument(ProcessStartInfo startInfo, string value)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(value);
        startInfo.ArgumentList.Add(value);
    }

    private sealed record ProcessExecutionResult(int ExitCode,
                                                 string StandardOutput,
                                                 string StandardError);

    // See the note on PatchAppSettingsTests.smWaitForExitTimeout: this budget exists to stop
    // a hung interpreter from wedging the suite, not to time the script.
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(120);

    private const string WindowsOnlySkipReason = "The Windows MSI settings patch requires Windows.";
    private const string RepositoryMissingSkipReason = "The repository source tree is not available.";
    private const string InstallerDirectoryName = "SaddleRAG.Installer";
    private const string PatchScriptFileName = "PatchAppSettings.ps1";
    private const string WindowsPowerShellExecutable = "powershell.exe";
    private const string NoProfileArgument = "-NoProfile";
    private const string ExecutionPolicyArgument = "-ExecutionPolicy";
    private const string BypassArgument = "Bypass";
    private const string FileArgument = "-File";
    private const string AppSettingsPathArgument = "-AppSettingsPath";
    private const string ConnectionStringArgument = "-ConnectionString";
    private const string DatabaseNameArgument = "-DatabaseName";
    private const string OllamaEndpointArgument = "-OllamaEndpoint";
    private const string DoclingEndpointArgument = "-DoclingEndpoint";
    private const string ExecutionProviderArgument = "-ExecutionProvider";
    private const string EscapeFailedArgument = "-EscapeFailed";
    private const string DefaultMongoConnection = "mongodb://localhost:27017";
    private const string DefaultDatabaseName = "SaddleRAG";
    private const string DefaultOllamaEndpoint = "http://localhost:11434";
    private const string DefaultExecutionProvider = "Cpu";
    private const string TempFileSuffix = ".tmp";
}
