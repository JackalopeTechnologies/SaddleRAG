// PatchAppSettingsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
                           connectionString: "mongodb://example:27017",
                           databaseName: "SaddleRAGTest",
                           ollamaEndpoint: "http://example:11434",
                           executionProvider: "DirectMl"
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
                           connectionString: "mongodb://localhost:27017",
                           databaseName: "SaddleRAG",
                           ollamaEndpoint: "http://localhost:11434",
                           executionProvider: string.Empty
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
                           connectionString: "mongodb://localhost:27017",
                           databaseName: "SaddleRAG",
                           ollamaEndpoint: "http://localhost:11434",
                           executionProvider: "   "
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
                                         connectionString: "x",
                                         databaseName: "x",
                                         ollamaEndpoint: "x",
                                         executionProvider: "Cpu"
                                        );

        Assert.NotEqual(expected: 0, actual: exitCode);
        Assert.False(File.Exists(missing), "Script must not create the target file on failure.");
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
                           connectionString: "mongodb://example:27017",
                           databaseName: "SaddleRAGTest",
                           ollamaEndpoint: "http://example:11434",
                           executionProvider: "DirectMl"
                          );

            JsonNode patched = LoadJson(fixturePath);

            // Sections the script doesn't touch must round-trip unchanged.
            Assert.Equal("Information", (string?) patched["Logging"]?["LogLevel"]?["Default"]);
            Assert.Equal("http://kestrel:6100", (string?) patched["Kestrel"]?["Endpoints"]?["Http"]?["Url"]);
            Assert.Equal(0.4d, (double?) patched["Ranking"]?["Bm25Weight"]);

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
        // (Logging + Kestrel + Ranking siblings, multiple Mongo profiles).
        // The script edits only the four properties it owns; everything
        // else must round-trip untouched.
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
            ["Kestrel"] = new JsonObject
            {
                ["Endpoints"] = new JsonObject
                {
                    ["Http"] = new JsonObject { ["Url"] = "http://kestrel:6100" }
                }
            },
            ["MongoDB"] = new JsonObject
            {
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
            ["Ollama"] = new JsonObject { ["Endpoint"] = "http://placeholder" },
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
        int exitCode = TryRunPatchScript(appSettingsPath, connectionString, databaseName, ollamaEndpoint, executionProvider);
        if (exitCode != 0)
            throw new InvalidOperationException($"PatchAppSettings.ps1 exited with code {exitCode}.");
    }

    private static int TryRunPatchScript(string appSettingsPath,
                                         string connectionString,
                                         string databaseName,
                                         string ollamaEndpoint,
                                         string executionProvider)
    {
        string? scriptPath = TryResolveScriptPath();
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
                                executionProvider
                               );
    }

    private static int InvokePowerShell(string scriptPath,
                                        string appSettingsPath,
                                        string connectionString,
                                        string databaseName,
                                        string ollamaEndpoint,
                                        string executionProvider)
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

        using Process? proc = Process.Start(startInfo);
        if (proc == null)
            throw new InvalidOperationException("Failed to start powershell.exe");

        // Drain stdout/stderr asynchronously BEFORE WaitForExit so a verbose
        // error message can't fill the ~4KB pipe buffer and deadlock the
        // child + the test. WaitForExit gets a bounded timeout so a hung
        // powershell.exe (e.g., parameter binding prompts) fails as a clear
        // assertion rather than a CI hang.
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

        bool exited = proc.WaitForExit(smWaitForExitTimeout);
        Assert.True(exited, "powershell.exe did not exit within the expected window; likely deadlocked or waiting on a prompt.");

        // Joining the read tasks after WaitForExit is safe and surfaces
        // their output for the assertion message below if we ever decide
        // to attach it (kept available for future diagnostics).
        stdoutTask.Wait();
        stderrTask.Wait();

        return proc.ExitCode;
    }

    private static string? TryResolveScriptPath()
    {
        string testBinDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        DirectoryInfo? dir = new DirectoryInfo(testBinDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, RepositoryRootMarker)))
            dir = dir.Parent;
        string? result = null;
        if (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, InstallerFolderName, PatchScriptFileName);
            if (File.Exists(candidate))
                result = candidate;
        }
        return result;
    }

    private static readonly TimeSpan smWaitForExitTimeout = TimeSpan.FromSeconds(30);

    private const string WindowsOnlySkipReason =
        "PatchAppSettings.ps1 is invoked by the Windows MSI installer; the test requires powershell.exe.";
    private const string ScriptMissingSkipReason =
        "PatchAppSettings.ps1 not locatable from test binary directory; the test requires the script to be present in the source tree.";
    private const string RepositoryRootMarker = "SaddleRAG.slnx";
    private const string InstallerFolderName = "SaddleRAG.Installer";
    private const string PatchScriptFileName = "PatchAppSettings.ps1";
}
