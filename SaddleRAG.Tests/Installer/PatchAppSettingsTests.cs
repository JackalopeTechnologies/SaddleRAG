// PatchAppSettingsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

#endregion

namespace SaddleRAG.Tests.Installer;

/// <summary>
///     Drives the production <c>PatchAppSettings.ps1</c> shipped by the
///     installer against a fixture appsettings.json so the MSI's
///     config-rewrite step is exercised outside of an actual install.
///     The script is the same file the WiX <c>SetProperty</c> at
///     <c>SaddleRAG.Installer/Package.wxs</c> invokes via
///     <c>powershell.exe -File</c>; if these tests diverge from
///     production, the WiX file is the side that's wrong.
///     Skips on non-Windows hosts (SaddleRAG ships only on Windows, but
///     contributors occasionally run the test suite from WSL / macOS).
/// </summary>
public sealed class PatchAppSettingsTests
{
    [Fact]
    public void WritesAllFourPropertiesIntoAppSettings()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        string fixturePath = CreateFixture();
        try
        {
            RunPatchScript(fixturePath,
                           "mongodb://example:27017",
                           "SaddleRAGTest",
                           "http://example:11434",
                           "DirectMl"
                          );

            JsonNode patched = LoadJson(fixturePath);
            Assert.Equal("mongodb://example:27017", (string?) patched["MongoDB"]?["Profiles"]?["local"]?["ConnectionString"]);
            Assert.Equal("SaddleRAGTest", (string?) patched["MongoDB"]?["Profiles"]?["local"]?["DatabaseName"]);
            Assert.Equal("http://example:11434", (string?) patched["Ollama"]?["Endpoint"]);
            Assert.Equal("DirectMl", (string?) patched["Onnx"]?["ExecutionProvider"]);
        }
        finally
        {
            File.Delete(fixturePath);
        }
    }

    [Fact]
    public void EmptyExecutionProviderFallsBackToCpu()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        string fixturePath = CreateFixture();
        try
        {
            RunPatchScript(fixturePath,
                           "mongodb://localhost:27017",
                           "SaddleRAG",
                           "http://localhost:11434",
                           string.Empty
                          );

            JsonNode patched = LoadJson(fixturePath);
            Assert.Equal("Cpu", (string?) patched["Onnx"]?["ExecutionProvider"]);
        }
        finally
        {
            File.Delete(fixturePath);
        }
    }

    [Fact]
    public void WhitespaceExecutionProviderFallsBackToCpu()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        string fixturePath = CreateFixture();
        try
        {
            RunPatchScript(fixturePath,
                           "mongodb://localhost:27017",
                           "SaddleRAG",
                           "http://localhost:11434",
                           "   "
                          );

            JsonNode patched = LoadJson(fixturePath);
            Assert.Equal("Cpu", (string?) patched["Onnx"]?["ExecutionProvider"]);
        }
        finally
        {
            File.Delete(fixturePath);
        }
    }

    [Fact]
    public void MissingFixtureExitsNonZero()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        string missing = Path.Combine(Path.GetTempPath(), $"saddlerag-missing-{Guid.NewGuid()}.json");
        int exitCode = TryRunPatchScript(missing,
                                         "x",
                                         "x",
                                         "x",
                                         "Cpu"
                                        );

        Assert.NotEqual(expected: 0, exitCode);
        Assert.False(File.Exists(missing), "Script must not create the target file on failure.");
    }

    [Fact]
    public void FailureSentinelEmittedToStderr()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        // Forces the catch path by handing the script a fixture path that
        // doesn't exist. The .ps1's catch emits a stable "FAILURE:" sentinel
        // that MSI-log scrapers can grep for to distinguish ran-and-failed
        // from never-ran.
        string missing = Path.Combine(Path.GetTempPath(), $"saddlerag-missing-{Guid.NewGuid()}.json");
        int exitCode = TryRunPatchScript(missing,
                                         "x",
                                         "x",
                                         "x",
                                         "Cpu",
                                         out string stderr
                                        );

        Assert.NotEqual(expected: 0, exitCode);
        Assert.Contains(FailureSentinel, stderr);
    }

    [Fact]
    public void PreservesUnrelatedConfigSectionsAndAdditionalMongoProfiles()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        string fixturePath = CreateFixtureWithExtraSections();
        try
        {
            RunPatchScript(fixturePath,
                           "mongodb://example:27017",
                           "SaddleRAGTest",
                           "http://example:11434",
                           "DirectMl"
                          );

            JsonNode patched = LoadJson(fixturePath);

            // Top-level scalar sibling -- the JSON object-rebuild path
            // must preserve it.
            Assert.Equal("*", (string?) patched["AllowedHosts"]);

            // Object-tree siblings the script doesn't touch.
            Assert.Equal("Information", (string?) patched["Logging"]?["LogLevel"]?["Default"]);
            Assert.Equal("http://kestrel:6100", (string?) patched["Kestrel"]?["Endpoints"]?["Http"]?["Url"]);
            Assert.Equal(expected: 0.4d, (double?) patched["Ranking"]?["Bm25Weight"]);

            // High-risk siblings INSIDE the same objects the script edits:
            // MongoDB.ActiveProfile lives next to MongoDB.Profiles, and
            // Ollama.EmbeddingModel lives next to Ollama.Endpoint.
            Assert.Equal("local", (string?) patched["MongoDB"]?["ActiveProfile"]);
            Assert.Equal("nomic-embed-text", (string?) patched["Ollama"]?["EmbeddingModel"]);

            // Additional Mongo profile must survive even though the script
            // only edits MongoDB.Profiles.local.
            Assert.Equal("mongodb://prod-host:27017", (string?) patched["MongoDB"]?["Profiles"]?["production"]?["ConnectionString"]);
            Assert.Equal("SaddleRAGProd", (string?) patched["MongoDB"]?["Profiles"]?["production"]?["DatabaseName"]);

            // And the script's own writes still apply.
            Assert.Equal("mongodb://example:27017", (string?) patched["MongoDB"]?["Profiles"]?["local"]?["ConnectionString"]);
            Assert.Equal("DirectMl", (string?) patched["Onnx"]?["ExecutionProvider"]);
        }
        finally
        {
            File.Delete(fixturePath);
        }
    }

    [Fact]
    public void OriginalFileUnchangedOnFailure()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        // The load-bearing claim of the .tmp + MoveFileEx pattern is that a
        // failure AT THE RENAME STEP (after the .tmp was successfully
        // written) leaves the ORIGINAL appsettings.json intact, not
        // truncated. Seed a well-formed fixture and force MoveFileEx to
        // fail by marking the target read-only -- ConvertFrom-Json /
        // property writes / Set-Content all succeed; MoveFileEx returns
        // false with ERROR_ACCESS_DENIED.
        string fixturePath = CreateFixture();
        File.SetAttributes(fixturePath, FileAttributes.ReadOnly);
        byte[] originalBytes = File.ReadAllBytes(fixturePath);

        try
        {
            int exitCode = TryRunPatchScript(fixturePath,
                                             "mongodb://example:27017",
                                             "SaddleRAGTest",
                                             "http://example:11434",
                                             "DirectMl"
                                            );

            Assert.NotEqual(expected: 0, exitCode);
            byte[] afterBytes = File.ReadAllBytes(fixturePath);
            Assert.Equal(originalBytes, afterBytes);
        }
        finally
        {
            File.SetAttributes(fixturePath, FileAttributes.Normal);
            File.Delete(fixturePath);
        }
    }

    [Fact]
    public void TempFileCleanedUpOnFailure()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        // Force a failure AFTER Set-Content has created the .tmp by marking
        // the target read-only so MoveFileEx fails. The catch block's
        // Remove-Item should clean up the .tmp regardless.
        string fixturePath = CreateFixture();
        string tempPath = fixturePath + TempFileSuffix;
        File.SetAttributes(fixturePath, FileAttributes.ReadOnly);

        try
        {
            int exitCode = TryRunPatchScript(fixturePath,
                                             "mongodb://example:27017",
                                             "SaddleRAGTest",
                                             "http://example:11434",
                                             "DirectMl"
                                            );

            Assert.NotEqual(expected: 0, exitCode);
            Assert.False(File.Exists(tempPath),
                         $"Catch path must remove the .tmp file; found leftover at '{tempPath}'."
                        );
        }
        finally
        {
            File.SetAttributes(fixturePath, FileAttributes.Normal);
            File.Delete(fixturePath);
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void EscapeFailedAbortsBeforeAnyWrite()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        // Wires the Critical-1 finding: when EscapeAppSettingsProperties.js
        // records a per-property failure in ESCAPE_FAILED, the patch script
        // must abort with a non-zero exit and emit the FAILURE: sentinel,
        // NOT silently write empty values into appsettings.json. Simulate
        // the failure by passing a non-empty -EscapeFailed directly.
        string fixturePath = CreateFixture();
        byte[] originalBytes = File.ReadAllBytes(fixturePath);

        try
        {
            int exitCode = TryRunPatchScript(fixturePath,
                                             "mongodb://example:27017",
                                             "SaddleRAGTest",
                                             "http://example:11434",
                                             "DirectMl",
                                             escapeFailed: SimulatedEscapeFailure,
                                             out string stderr
                                            );

            Assert.NotEqual(expected: 0, exitCode);
            Assert.Contains(FailureSentinel, stderr);
            Assert.Contains(SimulatedEscapeFailure, stderr);
            byte[] afterBytes = File.ReadAllBytes(fixturePath);
            Assert.Equal(originalBytes, afterBytes);
        }
        finally
        {
            File.Delete(fixturePath);
        }
    }

    private static string CreateFixture()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"saddlerag-appsettings-{Guid.NewGuid()}.json");
        var seed = new JsonObject
        {
            ["MongoDB"] = new JsonObject
            {
                ["Profiles"] = new JsonObject
                {
                    ["local"] = new JsonObject
                    {
                        ["ConnectionString"] = "mongodb://placeholder",
                        ["DatabaseName"] = "Placeholder"
                    }
                }
            },
            ["Ollama"] = new JsonObject { ["Endpoint"] = "http://placeholder" },
            ["Onnx"]   = new JsonObject { ["ExecutionProvider"] = "Cpu" }
        };
        File.WriteAllText(tempPath, seed.ToJsonString(), Encoding.UTF8);
        return tempPath;
    }

    private static string CreateFixtureWithExtraSections()
    {
        // Mirrors the shape of the shipped SaddleRAG.Mcp/appsettings.json
        // (top-level scalars, Logging + Kestrel + Ranking siblings, multiple
        // Mongo profiles, in-object siblings of edited fields). The script
        // edits only the four properties it owns; everything else must
        // round-trip untouched.
        string tempPath = Path.Combine(Path.GetTempPath(), $"saddlerag-appsettings-extra-{Guid.NewGuid()}.json");
        var seed = new JsonObject
        {
            ["Logging"] = new JsonObject
            {
                ["LogLevel"] = new JsonObject
                {
                    ["Default"] = "Information"
                }
            },
            ["AllowedHosts"] = "*",
            ["Kestrel"] = new JsonObject
            {
                ["Endpoints"] = new JsonObject
                {
                    ["Http"] = new JsonObject { ["Url"] = "http://kestrel:6100" }
                }
            },
            ["MongoDB"] = new JsonObject
            {
                ["ActiveProfile"] = "local",
                ["Profiles"] = new JsonObject
                {
                    ["local"] = new JsonObject
                    {
                        ["ConnectionString"] = "mongodb://placeholder",
                        ["DatabaseName"] = "Placeholder"
                    },
                    ["production"] = new JsonObject
                    {
                        ["ConnectionString"] = "mongodb://prod-host:27017",
                        ["DatabaseName"] = "SaddleRAGProd"
                    }
                }
            },
            ["Ollama"] = new JsonObject
            {
                ["Endpoint"] = "http://placeholder",
                ["EmbeddingModel"] = "nomic-embed-text"
            },
            ["Onnx"]   = new JsonObject { ["ExecutionProvider"] = "Cpu" },
            ["Ranking"] = new JsonObject { ["Bm25Weight"] = 0.4d }
        };
        File.WriteAllText(tempPath, seed.ToJsonString(), Encoding.UTF8);
        return tempPath;
    }

private static JsonNode LoadJson(string path)
    {
        string content = File.ReadAllText(path);
        JsonNode? parsed = JsonNode.Parse(content);
        if (parsed == null)
            throw new InvalidOperationException($"Failed to parse JSON at '{path}'.");
        return parsed;
    }

    private static void RunPatchScript(string appSettingsPath,
                                       string connectionString,
                                       string databaseName,
                                       string ollamaEndpoint,
                                       string executionProvider)
    {
        int exitCode = TryRunPatchScript(appSettingsPath,
                                         connectionString,
                                         databaseName,
                                         ollamaEndpoint,
                                         executionProvider,
                                         escapeFailed: string.Empty,
                                         out string stderr
                                        );
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"PatchAppSettings.ps1 exited with code {exitCode}. stderr: {stderr}"
            );
    }

    private static int TryRunPatchScript(string appSettingsPath,
                                         string connectionString,
                                         string databaseName,
                                         string ollamaEndpoint,
                                         string executionProvider)
    {
        return TryRunPatchScript(appSettingsPath,
                                 connectionString,
                                 databaseName,
                                 ollamaEndpoint,
                                 executionProvider,
                                 escapeFailed: string.Empty,
                                 out _
                                );
    }

    private static int TryRunPatchScript(string appSettingsPath,
                                         string connectionString,
                                         string databaseName,
                                         string ollamaEndpoint,
                                         string executionProvider,
                                         out string stderr)
    {
        return TryRunPatchScript(appSettingsPath,
                                 connectionString,
                                 databaseName,
                                 ollamaEndpoint,
                                 executionProvider,
                                 escapeFailed: string.Empty,
                                 out stderr
                                );
    }

    private static int TryRunPatchScript(string appSettingsPath,
                                         string connectionString,
                                         string databaseName,
                                         string ollamaEndpoint,
                                         string executionProvider,
                                         string escapeFailed,
                                         out string stderr)
    {
        string? scriptPath = InstallerSourceTreeResolver.TryResolveInstallerFile(PatchScriptFileName);
        if (scriptPath == null)
            Assert.Skip(ScriptMissingSkipReason);
        // Assert.Skip throws on the null branch; the second assertion narrows
        // scriptPath for the compiler without depending on a null-forgiving
        // operator (banned by the project analyzer).
        Assert.NotNull(scriptPath);
        return InvokePowerShell(scriptPath,
                                appSettingsPath,
                                connectionString,
                                databaseName,
                                ollamaEndpoint,
                                executionProvider,
                                escapeFailed,
                                out stderr
                               );
    }

    private static int InvokePowerShell(string scriptPath,
                                        string appSettingsPath,
                                        string connectionString,
                                        string databaseName,
                                        string ollamaEndpoint,
                                        string executionProvider,
                                        string escapeFailed,
                                        out string stderr)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-AppSettingsPath");
        startInfo.ArgumentList.Add(appSettingsPath);
        startInfo.ArgumentList.Add("-ConnectionString");
        startInfo.ArgumentList.Add(connectionString);
        startInfo.ArgumentList.Add("-DatabaseName");
        startInfo.ArgumentList.Add(databaseName);
        startInfo.ArgumentList.Add("-OllamaEndpoint");
        startInfo.ArgumentList.Add(ollamaEndpoint);
        startInfo.ArgumentList.Add("-ExecutionProvider");
        startInfo.ArgumentList.Add(executionProvider);
        startInfo.ArgumentList.Add("-EscapeFailed");
        startInfo.ArgumentList.Add(escapeFailed);

        using Process? proc = Process.Start(startInfo);
        if (proc == null)
            throw new InvalidOperationException("Failed to start powershell.exe");

        // Drain stdout/stderr asynchronously BEFORE WaitForExit so a verbose
        // error message can't fill the ~4KB pipe buffer and deadlock the
        // child + the test. WaitForExit gets a bounded timeout so a hung
        // powershell.exe (parameter-binding prompts, etc.) fails as a clear
        // assertion rather than a CI hang.
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

        bool exited = proc.WaitForExit(smWaitForExitTimeout);
        if (!exited)
        {
            // Bounded drain of whatever the still-running child emitted so the
            // assertion message is actually useful; if the child is fully
            // wedged the partial value is "(unread)".
            stdoutTask.Wait(smDiagnosticDrainTimeout);
            stderrTask.Wait(smDiagnosticDrainTimeout);
            string partialOut = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : UnreadDiagnosticPlaceholder;
            string partialErr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : UnreadDiagnosticPlaceholder;
            Assert.Fail($"powershell.exe did not exit within {smWaitForExitTimeout.TotalSeconds:N0}s. " +
                        $"stdout: {partialOut}; stderr: {partialErr}"
                       );
        }

        stdoutTask.Wait();
        stderrTask.Wait();
        stderr = stderrTask.Result;
        return proc.ExitCode;
    }

    private static readonly TimeSpan smWaitForExitTimeout = TimeSpan.FromSeconds(seconds: 30);
    private static readonly TimeSpan smDiagnosticDrainTimeout = TimeSpan.FromSeconds(seconds: 2);

    private const string WindowsOnlySkipReason =
        "PatchAppSettings.ps1 is invoked by the Windows MSI installer; the test requires powershell.exe.";
    private const string ScriptMissingSkipReason =
        "PatchAppSettings.ps1 not locatable from test binary directory; the test requires the script to be present in the source tree.";
    private const string PatchScriptFileName = "PatchAppSettings.ps1";
    private const string FailureSentinel = "PatchAppSettings: FAILURE:";
    private const string UnreadDiagnosticPlaceholder = "(unread)";
    private const string TempFileSuffix = ".tmp";
    private const string SimulatedEscapeFailure = "MONGOCONNECTION: simulated escape failure";
}
