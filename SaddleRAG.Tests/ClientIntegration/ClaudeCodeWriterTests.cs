// ClaudeCodeWriterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class ClaudeCodeWriterTests : IDisposable
{
    private readonly string mTempDir;
    private readonly string mConfigPath;
    private readonly string mSkillsBaseDir;
    private readonly string mFirstSkillPath;

    public ClaudeCodeWriterTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), "saddlerag-cc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mTempDir);
        mConfigPath = Path.Combine(mTempDir, ".claude.json");
        mSkillsBaseDir = Path.Combine(mTempDir, ".claude", "skills");
        mFirstSkillPath = Path.Combine(mSkillsBaseDir, "saddlerag-first", "SKILL.md");
    }

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
            Directory.Delete(mTempDir, recursive: true);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("no-mcp-section")]
    [InlineData("other-servers-only")]
    [InlineData("existing-saddlerag")]
    [InlineData("existing-permissions-allow")]
    public async Task RegisterMatchesFixtureExpectedOutput(string scenario)
    {
        string fixtureInput = TestPaths.FixtureFile("claude-code", scenario, "input.json");
        if (File.Exists(fixtureInput))
            File.Copy(fixtureInput, mConfigPath, overwrite: true);

        var writer = new ClaudeCodeWriter(mConfigPath, mSkillsBaseDir);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);

        string expected = await File.ReadAllTextAsync(
            TestPaths.FixtureFile("claude-code", scenario, "expected-after-register.json"),
            TestContext.Current.CancellationToken);
        string actual = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);

        Assert.Equal(NormalizeJson(expected), NormalizeJson(actual));
    }

    [Fact]
    public async Task RegisterDropsSkillFile()
    {
        var writer = new ClaudeCodeWriter(mConfigPath, mSkillsBaseDir);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(mFirstSkillPath));
        string content = await File.ReadAllTextAsync(mFirstSkillPath, TestContext.Current.CancellationToken);
        Assert.Contains("saddlerag-first", content);
    }

    [Fact]
    public async Task RegisterMalformedJsonReturnsFailureAndLeavesFileUntouched()
    {
        File.Copy(TestPaths.FixtureFile("claude-code", "malformed-json", "input.json"), mConfigPath, overwrite: true);
        string before = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);

        var writer = new ClaudeCodeWriter(mConfigPath, mSkillsBaseDir);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(mConfigPath, result.Message);

        string after = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task UnregisterRemovesSaddleRagOnly()
    {
        File.Copy(TestPaths.FixtureFile("claude-code", "other-servers-only", "input.json"), mConfigPath, overwrite: true);
        var writer = new ClaudeCodeWriter(mConfigPath, mSkillsBaseDir);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        var result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.WasNoOp);

        string actual = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);
        Assert.Contains("azure-devops", actual);
        Assert.DoesNotContain("saddlerag", actual);
        Assert.False(File.Exists(mFirstSkillPath));
    }

    [Fact]
    public async Task UnregisterMissingFileIsNoOp()
    {
        var writer = new ClaudeCodeWriter(mConfigPath, mSkillsBaseDir);

        var result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.WasNoOp);
    }

    private static string NormalizeJson(string raw)
    {
        return raw.Replace("\r\n", "\n").Trim();
    }
}
