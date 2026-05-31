// CopilotCliWriterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class CopilotCliWriterTests : IDisposable
{
    private readonly string mTempDir;
    private readonly string mConfigPath;
    private readonly string mSkillsBaseDir;
    private readonly string mFirstSkillPath;

    public CopilotCliWriterTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), "saddlerag-copilot-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mTempDir);
        mConfigPath = Path.Combine(mTempDir, ".copilot", "mcp-config.json");
        mSkillsBaseDir = Path.Combine(mTempDir, ".copilot", "skills");
        mFirstSkillPath = Path.Combine(mSkillsBaseDir, "saddlerag-first", "SKILL.md");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("COPILOT_HOME", value: null);
        if (Directory.Exists(mTempDir))
            Directory.Delete(mTempDir, recursive: true);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("other-servers-only")]
    [InlineData("existing-saddlerag")]
    public async Task RegisterMatchesFixture(string scenario)
    {
        string fixtureInput = TestPaths.FixtureFile("copilot-cli", scenario, "input.json");
        if (File.Exists(fixtureInput))
        {
            string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
            Directory.CreateDirectory(configDir);
            File.Copy(fixtureInput, mConfigPath, overwrite: true);
        }

        var writer = new CopilotCliWriter(mConfigPath, mSkillsBaseDir);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);

        string expected = await File.ReadAllTextAsync(
            TestPaths.FixtureFile("copilot-cli", scenario, "expected-after-register.json"),
            TestContext.Current.CancellationToken);
        string actual = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);

        Assert.Equal(NormalizeJson(expected), NormalizeJson(actual));
    }

    [Fact]
    public async Task RegisterDropsSkillFile()
    {
        var writer = new CopilotCliWriter(mConfigPath, mSkillsBaseDir);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(mFirstSkillPath));
        string content = await File.ReadAllTextAsync(mFirstSkillPath, TestContext.Current.CancellationToken);
        Assert.Contains("saddlerag-first", content);
    }

    [Fact]
    public async Task RegisterReplacesExistingSkillFileContent()
    {
        string skillDir = Path.GetDirectoryName(mFirstSkillPath) ?? mSkillsBaseDir;
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(mFirstSkillPath, "stale-skill", TestContext.Current.CancellationToken);

        var writer = new CopilotCliWriter(mConfigPath, mSkillsBaseDir);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(mFirstSkillPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("stale-skill", content);
        Assert.Contains("saddlerag-first", content);
    }

    [Fact]
    public async Task RegisterMalformedJsonReturnsFailureAndLeavesFileUntouched()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(configDir);
        File.Copy(TestPaths.FixtureFile("copilot-cli", "malformed-json", "input.json"), mConfigPath, overwrite: true);
        string before = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);

        var writer = new CopilotCliWriter(mConfigPath, mSkillsBaseDir);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(mConfigPath, result.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnregisterRemovesSaddleRagOnly()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(configDir);
        File.Copy(TestPaths.FixtureFile("copilot-cli", "other-servers-only", "input.json"), mConfigPath, overwrite: true);
        var writer = new CopilotCliWriter(mConfigPath, mSkillsBaseDir);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        var result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.WasNoOp);

        string actual = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);
        Assert.Contains("filesystem", actual);
        Assert.DoesNotContain("saddlerag", actual);
        Assert.False(File.Exists(mFirstSkillPath));
    }

    [Fact]
    public async Task UnregisterMissingFileIsNoOp()
    {
        var writer = new CopilotCliWriter(mConfigPath, mSkillsBaseDir);

        var result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.WasNoOp);
    }

    [Fact]
    public void RegisterUsesCopilotHomeOverrideWhenSet()
    {
        string overrideDir = Path.Combine(mTempDir, "custom-copilot-home");
        Environment.SetEnvironmentVariable("COPILOT_HOME", overrideDir);

        CopilotCliWriter writer = CopilotCliWriter.ForCurrentUser();

        Assert.Contains(overrideDir, writer.ConfigPath);
        Assert.Contains(overrideDir, writer.SkillsBaseDir);
    }

    [Fact]
    public async Task RegisterWritesHttpType()
    {
        var writer = new CopilotCliWriter(mConfigPath, mSkillsBaseDir);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        string actual = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(actual);
        System.Text.Json.JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("mcpServers", out System.Text.Json.JsonElement servers));
        Assert.True(servers.TryGetProperty("saddlerag", out System.Text.Json.JsonElement entry));
        Assert.True(entry.TryGetProperty("type", out System.Text.Json.JsonElement typeEl));
        Assert.Equal("http", typeEl.GetString());
    }

    private static string NormalizeJson(string raw)
    {
        return raw.Replace("\r\n", "\n").Trim();
    }
}
