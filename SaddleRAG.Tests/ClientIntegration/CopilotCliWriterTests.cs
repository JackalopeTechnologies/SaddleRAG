// CopilotCliWriterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class CopilotCliWriterTests : IDisposable
{
    private readonly string mTempDir;
    private readonly string mConfigPath;
    private readonly string mSkillPath;

    public CopilotCliWriterTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), "saddlerag-copilot-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mTempDir);
        mConfigPath = Path.Combine(mTempDir, ".copilot", "mcp-config.json");
        mSkillPath = Path.Combine(mTempDir, ".copilot", "skills", "saddlerag-first", "SKILL.md");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("COPILOT_HOME", null);
        if (Directory.Exists(mTempDir))
        {
            Directory.Delete(mTempDir, recursive: true);
        }
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

        var writer = new CopilotCliWriter(mConfigPath, mSkillPath);
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
        var writer = new CopilotCliWriter(mConfigPath, mSkillPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(mSkillPath));
        string content = await File.ReadAllTextAsync(mSkillPath, TestContext.Current.CancellationToken);
        Assert.Contains("saddlerag-first", content);
    }

    [Fact]
    public async Task RegisterMalformedJsonReturnsFailureAndLeavesFileUntouched()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(configDir);
        File.Copy(TestPaths.FixtureFile("copilot-cli", "malformed-json", "input.json"), mConfigPath, overwrite: true);
        string before = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);

        var writer = new CopilotCliWriter(mConfigPath, mSkillPath);
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
        var writer = new CopilotCliWriter(mConfigPath, mSkillPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        var result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.WasNoOp);

        string actual = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);
        Assert.Contains("filesystem", actual);
        Assert.DoesNotContain("saddlerag", actual);
        Assert.False(File.Exists(mSkillPath));
    }

    [Fact]
    public async Task UnregisterMissingFileIsNoOp()
    {
        var writer = new CopilotCliWriter(mConfigPath, mSkillPath);

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
        Assert.Contains(overrideDir, writer.SkillPath);
    }

    private static string NormalizeJson(string raw)
    {
        return raw.Replace("\r\n", "\n").Trim();
    }
}
