// RestartComponentScriptTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Win32;

#endregion

namespace SaddleRAG.Tests.Installer;

/// <summary>
///     Pins the elevated service-control script the tray launches through a UAC prompt.
///     The script runs as administrator, so the source tests lock down what it may contain
///     (an allow-list and no dynamic code, no scheduled tasks, no service reconfiguration),
///     and the behaviour tests exercise only <c>-ValidateOnly</c>: they must never start or
///     stop a real service.
/// </summary>
[Collection(PowerShellScriptCollection.Name)]
public sealed class RestartComponentScriptTests
{
    [Fact]
    public void ScriptRestrictsActionToStartStopRestart()
    {
        string source = ReadScriptSource();

        Assert.Contains("[ValidateSet('Start','Stop','Restart')]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptNamesTheAllowListedServices()
    {
        string source = ReadScriptSource();

        Assert.Contains("mongod.exe", source, StringComparison.Ordinal);
        Assert.Contains("SaddleRAGMcp", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Invoke-Expression")]
    [InlineData("iex ")]
    [InlineData("Register-ScheduledTask")]
    [InlineData("schtasks")]
    [InlineData("Set-Service")]
    [InlineData("sc.exe config")]
    public void ScriptContainsNoForbiddenConstruct(string forbidden)
    {
        string source = ReadScriptSource();

        Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageWxsInstallsTheScriptWithTheHelperScripts()
    {
        string? wxsPath = InstallerSourceTreeResolver.TryResolveInstallerFile(PackageWxsFileName);
        if (wxsPath == null)
            Assert.Skip(RepositoryMissingSkipReason);
        Assert.NotNull(wxsPath);

        XDocument doc = XDocument.Load(wxsPath);
        XElement? helperScripts = doc.Descendants(smWix + "Component")
                                     .FirstOrDefault(e => (string?) e.Attribute("Id") == HelperScriptsComponentId);

        Assert.NotNull(helperScripts);
        Assert.Contains(helperScripts.Elements(smWix + "File"),
                        e => (string?) e.Attribute("Source") == ScriptFileName
                       );
    }

    [Fact]
    public void ServiceOutsideTheAllowListExitsTen()
    {
        int exitCode = RunScript(out _, "-ServiceName", "Spooler", "-Action", "Restart", "-ValidateOnly");

        Assert.Equal(ExitNotAllowListed, exitCode);
    }

    [Fact]
    public void MalformedServiceNameExitsTen()
    {
        int exitCode = RunScript(out _, "-ServiceName", "bad name;x", "-Action", "Start", "-ValidateOnly");

        Assert.Equal(ExitNotAllowListed, exitCode);
    }

    [Fact]
    public void CommaListContainingADisallowedServiceExitsTen()
    {
        // With -File, Windows PowerShell 5.1 delivers "A,B" as a single string; the script
        // must split it, and one disallowed name must reject the whole request.
        int exitCode = RunScript(out _,
                                 "-ServiceName",
                                 "SaddleRAGMcp,Spooler",
                                 "-Action",
                                 "Restart",
                                 "-ValidateOnly"
                                );

        Assert.Equal(ExitNotAllowListed, exitCode);
    }

    [Fact]
    public void DisallowedServiceListedFirstStillExitsTen()
    {
        int exitCode = RunScript(out _,
                                 "-ServiceName",
                                 "Spooler,SaddleRAGMcp",
                                 "-Action",
                                 "Restart",
                                 "-ValidateOnly"
                                );

        Assert.Equal(ExitNotAllowListed, exitCode);
    }

    [Fact]
    public void ServiceWithNoRegistrationExitsTen()
    {
        // A name with no service registration cannot be shown to run mongod.exe, so it is
        // not allow-listed (10) rather than merely missing (11).
        int exitCode = RunScript(out _,
                                 "-ServiceName",
                                 UnregisteredServiceName,
                                 "-Action",
                                 "Start",
                                 "-ValidateOnly"
                                );

        Assert.Equal(ExitNotAllowListed, exitCode);
    }

    [Fact]
    public void AllowListedMcpServiceValidatesAccordingToInstallation()
    {
        int exitCode = RunScript(out _, "-ServiceName", McpServiceName, "-Action", "Start", "-ValidateOnly");

        int expected = IsServiceRegistered(McpServiceName) ? ExitSucceeded : ExitNotInstalled;
        Assert.Equal(expected, exitCode);
    }

    [Fact]
    public void RepeatedAllowListedNameStillValidatesAccordingToInstallation()
    {
        int exitCode = RunScript(out _,
                                 "-ServiceName",
                                 $"{McpServiceName},{McpServiceName}",
                                 "-Action",
                                 "Restart",
                                 "-ValidateOnly"
                                );

        int expected = IsServiceRegistered(McpServiceName) ? ExitSucceeded : ExitNotInstalled;
        Assert.Equal(expected, exitCode);
    }

    [Fact]
    public void ValidateOnlyWritesNothingToTheActionLog()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                      LogFolderName,
                                      LogSubFolderName,
                                      LogFileName
                                     );
        long? sizeBefore = File.Exists(logPath) ? new FileInfo(logPath).Length : null;

        RunScript(out _, "-ServiceName", "Spooler", "-Action", "Restart", "-ValidateOnly");

        long? sizeAfter = File.Exists(logPath) ? new FileInfo(logPath).Length : null;
        Assert.Equal(sizeBefore, sizeAfter);
    }

    private static string ReadScriptSource()
    {
        string? scriptPath = InstallerSourceTreeResolver.TryResolveInstallerFile(ScriptFileName);
        if (scriptPath == null)
        {
            // Until the script exists this must fail rather than skip, so a missing file is RED.
            string? root = InstallerSourceTreeResolver.TryResolveRepositoryRoot();
            if (root == null)
                Assert.Skip(RepositoryMissingSkipReason);
            Assert.Fail($"{ScriptFileName} was not found under {InstallerFolderName}.");
        }

        Assert.NotNull(scriptPath);
        return File.ReadAllText(scriptPath);
    }

    private static bool IsServiceRegistered(string serviceName)
    {
        bool result = false;
        if (OperatingSystem.IsWindows())
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            result = key != null;
        }

        return result;
    }

    private static int RunScript(out string standardError, params string[] scriptArguments)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        string scriptPath = ReadScriptPath();
        var startInfo = new ProcessStartInfo(WindowsPowerShellExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // The same switches the tray's elevated launch uses.
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (string argument in scriptArguments)
            startInfo.ArgumentList.Add(argument);

        using Process? process = Process.Start(startInfo);
        if (process == null)
            throw new InvalidOperationException("Failed to start powershell.exe");

        // Drain both pipes before waiting so a verbose message cannot fill the buffer and
        // deadlock the child.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> errorOutput = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(smWaitForExitTimeout))
            Assert.Fail($"{ScriptFileName} did not exit within {smWaitForExitTimeout.TotalSeconds:N0}s.");
        standardOutput.Wait();
        errorOutput.Wait();
        standardError = errorOutput.Result;
        return process.ExitCode;
    }

    private static string ReadScriptPath()
    {
        string? scriptPath = InstallerSourceTreeResolver.TryResolveInstallerFile(ScriptFileName);
        if (scriptPath == null)
            Assert.Fail($"{ScriptFileName} was not found under {InstallerFolderName}.");
        Assert.NotNull(scriptPath);
        return scriptPath;
    }

    // Generous on purpose: the assertion is the exit code, not how quickly Windows
    // PowerShell 5.1 starts on a busy machine.
    private static readonly TimeSpan smWaitForExitTimeout = TimeSpan.FromSeconds(seconds: 120);

    private static readonly XNamespace smWix = "http://wixtoolset.org/schemas/v4/wxs";

    private const int ExitSucceeded = 0;
    private const int ExitNotAllowListed = 10;
    private const int ExitNotInstalled = 11;
    private const string ScriptFileName = "Restart-SaddleRAGComponent.ps1";
    private const string PackageWxsFileName = "Package.wxs";
    private const string InstallerFolderName = "SaddleRAG.Installer";
    private const string HelperScriptsComponentId = "HelperScripts";
    private const string McpServiceName = "SaddleRAGMcp";
    private const string UnregisteredServiceName = "SaddleRAGNoSuchServiceForTests";
    private const string WindowsPowerShellExecutable = "powershell.exe";
    private const string LogFolderName = "SaddleRAG";
    private const string LogSubFolderName = "logs";
    private const string LogFileName = "service-actions.log";
    private const string WindowsOnlySkipReason =
        "Restart-SaddleRAGComponent.ps1 is run by Windows PowerShell 5.1 through the tray; the test requires Windows.";
    private const string RepositoryMissingSkipReason = "The repository source tree is not available.";
}
