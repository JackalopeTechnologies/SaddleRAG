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
        string scriptPath = ResolveScriptPath();
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
        proc.WaitForExit();
        return proc.ExitCode;
    }

    private static string ResolveScriptPath()
    {
        // Tests run from SaddleRAG.Tests/bin/.../net10.0/. The .ps1 source
        // lives alongside Package.wxs in SaddleRAG.Installer/, three
        // parents + a sibling-folder hop away.
        string? testBinDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        DirectoryInfo? dir = new DirectoryInfo(testBinDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SaddleRAG.slnx")))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException("Could not locate repository root from test binary directory.");
        string scriptPath = Path.Combine(dir.FullName, "SaddleRAG.Installer", "PatchAppSettings.ps1");
        if (!File.Exists(scriptPath))
            throw new InvalidOperationException($"PatchAppSettings.ps1 not found at '{scriptPath}'.");
        return scriptPath;
    }

    private const string WindowsOnlySkipReason = "PatchAppSettings.ps1 is invoked by the Windows MSI installer; the test requires powershell.exe.";
}
