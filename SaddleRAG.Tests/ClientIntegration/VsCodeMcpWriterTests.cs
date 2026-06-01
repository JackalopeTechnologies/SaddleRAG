// VsCodeMcpWriterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json.Nodes;
using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class VsCodeMcpWriterTests : IDisposable
{
    private readonly string mTempDir;
    private readonly string mConfigPath;
    private readonly string mSettingsPath;
    private readonly string mPluginRoot;
    private readonly string mPluginManifestPath;
    private readonly string mPluginMcpPath;

    public VsCodeMcpWriterTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), "saddlerag-vsc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mTempDir);
        mConfigPath = Path.Combine(mTempDir, "Code", "User", "mcp.json");
        mSettingsPath = Path.Combine(mTempDir, "Code", "User", "settings.json");
        mPluginRoot = Path.Combine(mTempDir, "Code", "User", "saddlerag-plugin");
        mPluginManifestPath = Path.Combine(mPluginRoot, "plugin.json");
        mPluginMcpPath = Path.Combine(mPluginRoot, ".mcp.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
            Directory.Delete(mTempDir, recursive: true);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("other-servers-only")]
    [InlineData("existing-saddlerag")]
    public async Task RegisterCreatesPluginAndCleansLegacyMcpEntry(string scenario)
    {
        string fixtureInput = TestPaths.FixtureFile("vscode-mcp", scenario, "input.json");
        if (File.Exists(fixtureInput))
        {
            string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
            Directory.CreateDirectory(configDir);
            File.Copy(fixtureInput, mConfigPath, overwrite: true);
        }

        var writer = new VsCodeMcpWriter(mConfigPath);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(mPluginManifestPath));
        Assert.True(File.Exists(mPluginMcpPath));

        JsonObject pluginManifest = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(mPluginManifestPath, TestContext.Current.CancellationToken)));
        Assert.Equal("jackalope-saddlerag", pluginManifest["name"]?.GetValue<string>());
        Assert.Equal(".mcp.json", pluginManifest["mcpServers"]?.GetValue<string>());

        JsonObject pluginMcp = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(mPluginMcpPath, TestContext.Current.CancellationToken)));
        JsonObject servers = Assert.IsType<JsonObject>(pluginMcp["mcpServers"]);
        JsonObject saddleRag = Assert.IsType<JsonObject>(servers["saddlerag"]);
        Assert.Equal("http://localhost:6100/mcp", saddleRag["url"]?.GetValue<string>());

                JsonObject config = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken)));
                JsonObject configServers = (config["servers"] as JsonObject) ?? new JsonObject();
                Assert.DoesNotContain(configServers, entry => string.Equals(entry.Key, "saddlerag", StringComparison.OrdinalIgnoreCase));
        if (string.Equals(scenario, "other-servers-only", StringComparison.Ordinal))
                        Assert.Contains(configServers, entry => string.Equals(entry.Key, "github", StringComparison.Ordinal));
    }

        [Fact]
        public async Task RegisterRemovesLegacyMcpEntryRegardlessOfCase()
        {
                string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
                Directory.CreateDirectory(configDir);
                string before = """
                                                {
                                                    "servers": {
                                                        "SaddleRag": {
                                                            "type": "http",
                                                            "url": "http://localhost:6100"
                                                        },
                                                        "github": {
                                                            "type": "http",
                                                            "url": "https://api.githubcopilot.com/mcp"
                                                        }
                                                    }
                                                }
                                                """;
                await File.WriteAllTextAsync(mConfigPath, before, TestContext.Current.CancellationToken);

                var writer = new VsCodeMcpWriter(mConfigPath);
                RegisterResult result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

                Assert.True(result.Success, result.Message);

                JsonObject config = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken)));
                JsonObject configServers = (config["servers"] as JsonObject) ?? new JsonObject();
                Assert.DoesNotContain(configServers, entry => string.Equals(entry.Key, "saddlerag", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(configServers, entry => string.Equals(entry.Key, "github", StringComparison.Ordinal));
        }

    [Fact]
    public async Task RegisterCreatesParentDirectoryIfMissing()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Assert.False(Directory.Exists(configDir));

        var writer = new VsCodeMcpWriter(mConfigPath);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(File.Exists(mConfigPath));
        Assert.True(File.Exists(mPluginManifestPath));
        Assert.True(File.Exists(mSettingsPath));
    }

    [Fact]
    public async Task RegisterConfiguresCopilotAutostartSkillDiscoveryAndPluginLocation()
    {
        string settingsDir = Path.GetDirectoryName(mSettingsPath) ?? mTempDir;
        Directory.CreateDirectory(settingsDir);
        string before = """
                        {
                          "workbench.colorTheme": "PowerShell ISE",
                                                    "chat.mcp.autostart": "never",
                          "chat.agentSkillsLocations": {
                            "~/.other/skills": true
                          }
                        }
                        """;
        await File.WriteAllTextAsync(mSettingsPath, before, TestContext.Current.CancellationToken);

        var writer = new VsCodeMcpWriter(mConfigPath);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);

        string settingsText = await File.ReadAllTextAsync(mSettingsPath, TestContext.Current.CancellationToken);
        JsonObject settings = Assert.IsType<JsonObject>(JsonNode.Parse(settingsText));
        Assert.Equal("always", settings["chat.mcp.autoStart"]?.GetValue<string>());
        Assert.Equal("always", settings["chat.mcp.autostart"]?.GetValue<string>());
        Assert.True(settings["chat.useAgentSkills"]?.GetValue<bool>());
        Assert.True(settings["chat.plugins.enabled"]?.GetValue<bool>());

        JsonObject skillLocations = Assert.IsType<JsonObject>(settings["chat.agentSkillsLocations"]);
        Assert.True(skillLocations["~/.copilot/skills"]?.GetValue<bool>());
        Assert.True(skillLocations["~/.claude/skills"]?.GetValue<bool>());
        Assert.True(skillLocations["~/.other/skills"]?.GetValue<bool>());

        JsonObject pluginLocations = Assert.IsType<JsonObject>(settings["chat.pluginLocations"]);
        Assert.True(pluginLocations[mPluginRoot]?.GetValue<bool>());
    }

    [Fact]
    public async Task GetStatusReportsPluginPathAndEnabledState()
    {
        var writer = new VsCodeMcpWriter(mConfigPath);
        RegisterResult registerResult = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(registerResult.Success, registerResult.Message);

        StatusResult status = await writer.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(mPluginManifestPath, status.ConfigPath);
        Assert.Equal(mPluginRoot, status.PluginPath);
        Assert.True(status.PluginEnabled);
        Assert.True(status.AgentPluginsEnabled);
        Assert.True(status.SaddleRagEntryPresent);
        Assert.True(status.EndpointMatchesCanonical);
        Assert.Equal(string.Empty, status.Notes);
    }

    [Fact]
    public async Task RegisterMalformedSettingsJsonReturnsFailureAndLeavesPluginAndMcpUntouched()
    {
        string settingsDir = Path.GetDirectoryName(mSettingsPath) ?? mTempDir;
        Directory.CreateDirectory(settingsDir);
        await File.WriteAllTextAsync(mSettingsPath, "{ broken json", TestContext.Current.CancellationToken);

        var writer = new VsCodeMcpWriter(mConfigPath);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(File.Exists(mPluginManifestPath));
        Assert.False(File.Exists(mPluginMcpPath));
        Assert.False(File.Exists(mConfigPath));
    }

    [Fact]
    public async Task RegisterMalformedJsonReturnsFailureAndLeavesFileUntouched()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(configDir);
        File.Copy(TestPaths.FixtureFile("vscode-mcp", "malformed-json", "input.json"), mConfigPath, overwrite: true);
        string before = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);

        var writer = new VsCodeMcpWriter(mConfigPath);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnregisterRemovesSaddleRagOnly()
    {
        string unregisterDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(unregisterDir);
        File.Copy(TestPaths.FixtureFile("vscode-mcp", "other-servers-only", "input.json"), mConfigPath, overwrite: true);
        var writer = new VsCodeMcpWriter(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        var result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(mPluginRoot));
        string settingsText = await File.ReadAllTextAsync(mSettingsPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(mPluginRoot, settingsText);
        Assert.Contains("github", await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("saddlerag", await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnregisterMissingFileIsNoOp()
    {
        var writer = new VsCodeMcpWriter(mConfigPath);

        var result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.WasNoOp);
    }

    private static string NormalizeJson(string raw)
    {
        return raw.Replace("\r\n", "\n").Trim();
    }
}
