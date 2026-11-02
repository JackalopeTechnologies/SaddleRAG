// RestartComponentScriptTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Diagnostics;
using System.Text;
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

    [Theory]
    [InlineData("Mongo*")]
    [InlineData("[M]ongoDB")]
    [InlineData("*")]
    [InlineData("MongoDB?")]
    public void WildcardServiceNameExitsTen(string serviceName)
    {
        // A wildcard must never expand to a real service (for example MongoDB) and so pass
        // the allow-list through the service lookup.
        int exitCode = RunScript(out _, "-ServiceName", serviceName, "-Action", "Restart", "-ValidateOnly");

        Assert.Equal(ExitNotAllowListed, exitCode);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\MongoDB\Server\8.2\bin\mongod.exe"" --config ""C:\x y\mongod.cfg"" --service", "mongod.exe")]
    [InlineData(@"C:\MongoDB\bin\mongod.exe --config C:\MongoDB\mongod.cfg --service", "mongod.exe")]
    [InlineData(@"C:\MongoDB\bin\MONGOD.EXE", "MONGOD.EXE")]
    [InlineData(@"C:\Windows\tool -log C:\data\mongod.exe", "tool")]
    [InlineData(@"C:\Windows\tool.com -x C:/d/mongod.exe", "tool.com")]
    [InlineData(@"""C:\evil\mongod.exe.bat""", "mongod.exe.bat")]
    [InlineData(@"C:\Program Files\MongoDB\Server\8.2\bin\mongod.exe --service", "Program")]
    [InlineData(@"""C:\evil\tool.exe""mongod.exe", "")]
    public void ImagePathParserExtractsOnlyTheLaunchedExecutable(string imagePath, string expectedFileName)
    {
        // Diagnostic mode: parses the string and prints the executable file name; it never
        // reads the registry or touches a service.
        ScriptRun run = RunScriptCapture(new Dictionary<string, string>(),
                                         "-TestImagePath",
                                         imagePath
                                        );

        Assert.Equal(ExitSucceeded, run.ExitCode);
        Assert.Equal(expectedFileName, run.StandardOutput.Trim());
    }

    [Fact]
    public void ImagePathWithANoBreakSpaceAfterTheExecutableIsNotMongod()
    {
        // Windows command-line parsing splits only on space and tab, so a no-break space
        // (U+00A0) does not end the token: the launched path is not mongod.exe.
        ScriptRun run = RunScriptCapture(new Dictionary<string, string>(),
                                         "-TestImagePath",
                                         @"C:\MongoDB\bin\mongod.exe" + NoBreakSpace + "--service"
                                        );

        Assert.Equal(ExitSucceeded, run.ExitCode);
        Assert.NotEqual("mongod.exe", run.StandardOutput.Trim());
    }

    [Fact]
    public void ModuleAutoloadingIsDisabledBeforeAnyCommandRuns()
    {
        string[] lines = ReadScriptSource().Split('\n');
        int paramEnd = Array.FindIndex(lines, l => l.TrimEnd('\r') == ")");
        Assert.True(paramEnd >= 0, "The param block was not found.");

        string firstStatement = lines.Skip(paramEnd + 1)
                                     .Select(l => l.Trim())
                                     .First(l => l.Length > 0 && !l.StartsWith('#'));

        Assert.Equal("$PSModuleAutoloadingPreference = 'None'", firstStatement);
    }

    [Theory]
    [InlineData("Get-Service")]
    [InlineData("Add-Type")]
    [InlineData("Add-Content")]
    [InlineData("New-Item")]
    [InlineData("Test-Path")]
    [InlineData("Join-Path")]
    [InlineData("Write-Output")]
    [InlineData("Get-ItemProperty")]
    [InlineData("Get-ChildItem")]
    [InlineData("Out-Null")]
    public void ScriptUsesNoModuleCommand(string command)
    {
        // With autoloading off these would not resolve; more importantly none may be used,
        // because an elevated lookup could otherwise be satisfied from a user-writable module.
        string code = string.Join('\n',
                                  ReadScriptSource().Split('\n').Where(l => !l.TrimStart().StartsWith('#'))
                                 );

        Assert.DoesNotContain(command, code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScriptRecordsActionsInTheEventLogNotAFile()
    {
        string source = ReadScriptSource();

        Assert.Contains(EventSourceName, source, StringComparison.Ordinal);
        Assert.DoesNotContain("service-actions.log", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CommonApplicationData", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageWxsRegistersTheEventSourceInTheApplicationLog()
    {
        string? wxsPath = InstallerSourceTreeResolver.TryResolveInstallerFile(PackageWxsFileName);
        if (wxsPath == null)
            Assert.Skip(RepositoryMissingSkipReason);
        Assert.NotNull(wxsPath);

        XDocument doc = XDocument.Load(wxsPath);
        XElement? eventSource = doc.Descendants(smWixUtil + "EventSource")
                                   .FirstOrDefault(e => (string?) e.Attribute("Name") == EventSourceName);

        Assert.NotNull(eventSource);
        Assert.Equal("Application", (string?) eventSource.Attribute("Log"));
        Assert.False(string.IsNullOrEmpty((string?) eventSource.Attribute("EventMessageFile")));
    }

    [Fact]
    public void ShadowedModuleCommandsAreNeverRunByTheElevatedScript()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        // A user-writable module folder on PSModulePath that exports look-alikes of commands
        // the script once used. The script, running elevated, must not execute any of them.
        string root = Path.Combine(Path.GetTempPath(), $"saddlerag-shadow-{Guid.NewGuid():N}");
        string markerPath = Path.Combine(root, "marker.txt");
        try
        {
            string moduleRoot = CreateShadowModules(root);
            var environment = new Dictionary<string, string>
            {
                [ShadowMarkerVariable] = markerPath,
                ["PSModulePath"] = moduleRoot + Path.PathSeparator + Environment.GetEnvironmentVariable("PSModulePath")
            };

            // Control: a plain PowerShell session does pick the shadow up, so the test can fail.
            RunPowerShellCommand(environment, "Get-Service -Name Spooler");
            Assert.True(File.Exists(markerPath), "The shadow module was not loaded; the test is not exercising anything.");
            File.Delete(markerPath);

            ScriptRun run = RunScriptCapture(environment,
                                             "-ServiceName",
                                             McpServiceName,
                                             "-Action",
                                             "Restart",
                                             "-ValidateOnly"
                                            );

            Assert.False(File.Exists(markerPath),
                         $"A shadowed command ran inside the script. marker: {ReadIfExists(markerPath)}"
                        );
            Assert.Equal(IsServiceRegistered(McpServiceName) ? ExitSucceeded : ExitNotInstalled, run.ExitCode);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
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
        ScriptRun run = RunScriptCapture(new Dictionary<string, string>(), scriptArguments);
        standardError = run.StandardError;
        return run.ExitCode;
    }

    private static ScriptRun RunScriptCapture(IReadOnlyDictionary<string, string> environment,
                                              params string[] scriptArguments)
    {
        string scriptPath = ReadScriptPath();

        // The same switches the tray's elevated launch uses.
        var arguments = new List<string>
        {
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath
        };
        arguments.AddRange(scriptArguments);
        return RunPowerShell(environment, arguments);
    }

    private static void RunPowerShellCommand(IReadOnlyDictionary<string, string> environment, string command)
    {
        RunPowerShell(environment, ["-NoProfile", "-NonInteractive", "-Command", command]);
    }

    private static ScriptRun RunPowerShell(IReadOnlyDictionary<string, string> environment,
                                           IEnumerable<string> arguments)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        var startInfo = new ProcessStartInfo(WindowsPowerShellExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (KeyValuePair<string, string> variable in environment)
            startInfo.Environment[variable.Key] = variable.Value;

        using Process? process = Process.Start(startInfo);
        if (process == null)
            throw new InvalidOperationException("Failed to start powershell.exe");

        // Drain both pipes before waiting so a verbose message cannot fill the buffer and
        // deadlock the child.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> errorOutput = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(smWaitForExitTimeout))
            Assert.Fail($"powershell.exe did not exit within {smWaitForExitTimeout.TotalSeconds:N0}s.");
        standardOutput.Wait();
        errorOutput.Wait();
        return new ScriptRun(process.ExitCode, standardOutput.Result, errorOutput.Result);
    }

    // Writes a module folder whose manifest exports look-alikes of commands the script once
    // used; each look-alike records its name in the marker file and does nothing else.
    private static string CreateShadowModules(string root)
    {
        string moduleRoot = Path.Combine(root, "Modules");
        string moduleDirectory = Path.Combine(moduleRoot, ShadowModuleName);
        Directory.CreateDirectory(moduleDirectory);

        var body = new StringBuilder();
        foreach (string command in smShadowedCommands)
            body.AppendLine($"function {command} {{ [System.IO.File]::AppendAllText($env:{ShadowMarkerVariable}, '{command};') }}");
        File.WriteAllText(Path.Combine(moduleDirectory, ShadowModuleName + ".psm1"), body.ToString());

        string exported = string.Join(",", smShadowedCommands.Select(c => $"'{c}'"));
        File.WriteAllText(Path.Combine(moduleDirectory, ShadowModuleName + ".psd1"),
                          $"@{{ ModuleVersion = '1.0'; RootModule = '{ShadowModuleName}.psm1'; FunctionsToExport = @({exported}) }}"
                         );
        return moduleRoot;
    }

    private static string ReadIfExists(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private sealed record ScriptRun(int ExitCode, string StandardOutput, string StandardError);

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
    private static readonly XNamespace smWixUtil = "http://wixtoolset.org/schemas/v4/wxs/util";

    private static readonly string[] smShadowedCommands =
    [
        "Get-Service", "Add-Type", "Add-Content", "New-Item", "Test-Path", "Join-Path", "Write-Output",
        "Get-ItemProperty", "Get-ChildItem", "Out-Null", "Set-StrictMode"
    ];

    private const string NoBreakSpace = "\u00A0";
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
    private const string EventSourceName = "SaddleRAG Service Actions";
    private const string ShadowModuleName = "SaddleRAGShadow";
    private const string ShadowMarkerVariable = "SADDLERAG_SHADOW_MARKER";
    private const string WindowsOnlySkipReason =
        "Restart-SaddleRAGComponent.ps1 is run by Windows PowerShell 5.1 through the tray; the test requires Windows.";
    private const string RepositoryMissingSkipReason = "The repository source tree is not available.";
}
