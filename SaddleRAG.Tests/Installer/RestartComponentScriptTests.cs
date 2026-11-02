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
    [InlineData(@"%windir%\system32\svchost.exe -k netsvcs -p", "svchost.exe")]
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
    public void ImagePathIsReadUnexpandedSoTheCallersEnvironmentCannotWidenTheAllowList()
    {
        // seclogon's ImagePath is %windir%\system32\svchost.exe -k netsvcs -p (REG_EXPAND_SZ). If the
        // script let the registry expand it with its own environment, a windir of "C:\mongod.exe "
        // would make it read as mongod.exe and allow-list a Windows service.
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);
        if (!IsServiceRegistered(ExpandedImagePathServiceName))
            Assert.Skip($"The {ExpandedImagePathServiceName} service is not registered on this machine.");

        var environment = new Dictionary<string, string> { ["windir"] = MongodWindirOverride };
        ScriptRun run = RunScriptCapture(environment,
                                         "-ServiceName",
                                         ExpandedImagePathServiceName,
                                         "-Action",
                                         "Stop",
                                         "-ValidateOnly"
                                        );

        Assert.Equal(ExitNotAllowListed, run.ExitCode);
    }

    [Fact]
    public void ScriptReadsTheImagePathWithoutExpandingEnvironmentNames()
    {
        string source = ReadScriptSource();

        Assert.Contains("[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayServiceLocatorReadsTheImagePathWithoutExpandingEnvironmentNames()
    {
        // The tray must offer only what the script accepts, so it reads the same unexpanded text.
        // SaddleRAG.Tests does not reference the WPF tray project, so the source is pinned.
        string? root = InstallerSourceTreeResolver.TryResolveRepositoryRoot();
        if (root == null)
            Assert.Skip(RepositoryMissingSkipReason);
        Assert.NotNull(root);

        string source = File.ReadAllText(Path.Combine(root, "SaddleRAG.Tray", "Services", "RegistryWindowsServiceLocator.cs"));

        Assert.Contains("RegistryValueOptions.DoNotExpandEnvironmentNames", source, StringComparison.Ordinal);
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

    [Fact]
    public void ScriptWaitsForRunningOrStoppedWithoutACmdlet()
    {
        string source = ReadScriptSource();

        Assert.Contains("function Wait-ServiceSettled", source, StringComparison.Ordinal);
        Assert.Contains("[System.Threading.Thread]::Sleep($ServicePollMilliseconds)", source, StringComparison.Ordinal);
        Assert.Contains("'The service started, then stopped.'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Sleep", source, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Runs the script's own wait functions against a fake service. The script file is only parsed:
    ///     its function definitions and constant assignments are loaded, its body never runs, and no
    ///     real service is looked up or controlled. Each case lists the statuses the fake reports at
    ///     successive Refresh calls (the last one sticks), before its Start or Stop is called and,
    ///     after a '/', once it has been.
    /// </summary>
    [Theory]
    [InlineData("Start", "Stopped/StartPending,StartPending,Stopped", "failed", "Start")]
    [InlineData("Start", "Stopped/Stopped", "failed", "Start")]
    [InlineData("Start", "Stopped/StartPending,Running", "ok", "Start")]
    [InlineData("Start", "StopPending,StopPending,Stopped/StartPending,Running", "ok", "Start")]
    [InlineData("Start", "StartPending,StartPending,Stopped", "failed", "")]
    [InlineData("Stop", "StartPending,StartPending,Stopped", "ok", "")]
    [InlineData("Stop", "StartPending,Running/StopPending,Stopped", "ok", "Stop")]
    [InlineData("Stop", "Running/StopPending,Stopped", "ok", "Stop")]
    public void ServiceWaitsEndAtRunningOrStopped(string action, string statuses, string expectedOutcome, string expectedCalls)
    {
        FakeServiceRun run = RunWaitFunctions(action, statuses, FakeServiceTimeoutSeconds);

        Assert.Equal(expectedOutcome, run.Outcome);
        Assert.Equal(expectedCalls, run.Calls);
        Assert.True(run.Seconds < FakeServiceTimeoutSeconds, $"The wait took {run.Seconds}s; it should end at the first settled state.");
    }

    [Fact]
    public void AServiceThatNeverSettlesTimesOut()
    {
        FakeServiceRun run = RunWaitFunctions("Start", "Stopped/StartPending", timeoutSeconds: 1);

        Assert.Equal("timeout", run.Outcome);
    }

    private static FakeServiceRun RunWaitFunctions(string action, string statuses, int timeoutSeconds)
    {
        string scriptPath = ReadScriptPath();

        // Loads the System.ServiceProcess types before the fake (a class) is parsed, so the inner
        // block is built at run time. No cmdlet is used: the script's own constants switch module
        // autoloading off in this session too.
        string inner = $$"""
            class FakeService {
                [string]$ServiceName = 'FakeForTests'
                [System.ServiceProcess.ServiceControllerStatus]$Status
                [System.Collections.Generic.List[object]]$Phases = [System.Collections.Generic.List[object]]::new()
                [int]$Phase = 0
                [System.Collections.Generic.List[string]]$Calls = [System.Collections.Generic.List[string]]::new()
                FakeService([string]$statuses) {
                    foreach ($p in $statuses.Split('/')) {
                        $list = [System.Collections.Generic.List[string]]::new()
                        foreach ($s in $p.Split(',')) { $list.Add($s) }
                        $this.Phases.Add($list)
                    }
                    $this.Status = $this.Phases[0][0]
                }
                [void] Refresh() {
                    $sequence = $this.Phases[$this.Phase]
                    $this.Status = $sequence[0]
                    if ($sequence.Count -gt 1) { $sequence.RemoveAt(0) }
                }
                [void] Control([string]$name) {
                    $this.Calls.Add($name)
                    if ($this.Phase -lt $this.Phases.Count - 1) { $this.Phase++ }
                }
                [void] Start() { $this.Control('Start') }
                [void] Stop() { $this.Control('Stop') }
                [void] WaitForStatus([System.ServiceProcess.ServiceControllerStatus]$desired, [TimeSpan]$timeout) {
                    $watch = [System.Diagnostics.Stopwatch]::StartNew()
                    $this.Refresh()
                    while ($this.Status -ne $desired) {
                        if ($watch.Elapsed -ge $timeout) { throw [System.ServiceProcess.TimeoutException]::new('fake wait timed out') }
                        [System.Threading.Thread]::Sleep(50)
                        $this.Refresh()
                    }
                }
            }
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:{{ScriptPathVariable}}, [ref]$tokens, [ref]$errors)
            foreach ($statement in $ast.EndBlock.Statements) {
                $isFunction = $statement -is [System.Management.Automation.Language.FunctionDefinitionAst]
                $isConstant = $statement -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                              $statement.Right.Expression -is [System.Management.Automation.Language.ConstantExpressionAst]
                if ($isFunction -or $isConstant) { . ([scriptblock]::Create($statement.Extent.Text)) }
            }
            $service = [FakeService]::new('{{statuses}}')
            $timeout = [TimeSpan]::FromSeconds({{timeoutSeconds}})
            $watch = [System.Diagnostics.Stopwatch]::StartNew()
            $outcome = 'ok'
            try { {{action}}-TargetService -Service $service -Timeout $timeout }
            catch { if (Test-IsTimeout -Failure $_.Exception) { $outcome = 'timeout' } else { $outcome = 'failed' } }
            [Console]::Out.WriteLine("outcome=$outcome")
            [Console]::Out.WriteLine("calls=$($service.Calls -join ',')")
            [Console]::Out.WriteLine("seconds=$([int][Math]::Floor($watch.Elapsed.TotalSeconds))")
            """;
        string outer = "$ErrorActionPreference = 'Stop'\n" +
                       "$null = [System.Reflection.Assembly]::Load('System.ServiceProcess, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a')\n" +
                       $"& ([scriptblock]::Create([System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{Convert.ToBase64String(Encoding.UTF8.GetBytes(inner))}'))))\n";

        var environment = new Dictionary<string, string> { [ScriptPathVariable] = scriptPath };
        ScriptRun run = RunPowerShell(environment,
                                      ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(outer))]
                                     );

        Dictionary<string, string> values = run.StandardOutput
                                               .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                               .Where(line => line.Contains('='))
                                               .Select(line => line.Split('=', count: 2))
                                               .ToDictionary(parts => parts[0], parts => parts[1]);
        Assert.True(values.ContainsKey("outcome"), $"The wait harness reported nothing. stderr: {run.StandardError}");
        return new FakeServiceRun(values["outcome"], values["calls"], int.Parse(values["seconds"]));
    }

    private sealed record FakeServiceRun(string Outcome, string Calls, int Seconds);

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
    private const string ExpandedImagePathServiceName = "seclogon";
    private const string MongodWindirOverride = @"C:\mongod.exe ";
    private const string ScriptPathVariable = "SADDLERAG_RESTART_SCRIPT_PATH";
    private const int FakeServiceTimeoutSeconds = 5;
    private const string WindowsPowerShellExecutable = "powershell.exe";
    private const string EventSourceName = "SaddleRAG Service Actions";
    private const string ShadowModuleName = "SaddleRAGShadow";
    private const string ShadowMarkerVariable = "SADDLERAG_SHADOW_MARKER";
    private const string WindowsOnlySkipReason =
        "Restart-SaddleRAGComponent.ps1 is run by Windows PowerShell 5.1 through the tray; the test requires Windows.";
    private const string RepositoryMissingSkipReason = "The repository source tree is not available.";
}
