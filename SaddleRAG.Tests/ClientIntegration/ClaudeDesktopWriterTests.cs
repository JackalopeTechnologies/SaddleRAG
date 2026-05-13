// ClaudeDesktopWriterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class ClaudeDesktopWriterTests : IDisposable
{
    private readonly string mTempDir;
    private readonly string mConfigPath;

    public ClaudeDesktopWriterTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), "saddlerag-cd-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mTempDir);
        mConfigPath = Path.Combine(mTempDir, "claude_desktop_config.json");
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
    public async Task RegisterMatchesFixture(string scenario)
    {
        string fixtureInput = TestPaths.FixtureFile("claude-desktop", scenario, "input.json");
        if (File.Exists(fixtureInput))
            File.Copy(fixtureInput, mConfigPath, overwrite: true);

        var writer = new ClaudeDesktopWriter(mConfigPath);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);

        string expected = await File.ReadAllTextAsync(
            TestPaths.FixtureFile("claude-desktop", scenario, "expected-after-register.json"),
            TestContext.Current.CancellationToken);
        string actual = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);

        Assert.Equal(NormalizeJson(expected), NormalizeJson(actual));
    }

    [Fact]
    public async Task RegisterWritesFileWithoutBom()
    {
        var writer = new ClaudeDesktopWriter(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        byte[] bytes = await File.ReadAllBytesAsync(mConfigPath, TestContext.Current.CancellationToken);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..],
            "Claude Desktop rejects UTF-8 BOM; writer must not emit one.");
    }

    [Fact]
    public async Task RegisterMalformedJsonReturnsFailureAndLeavesFileUntouched()
    {
        File.Copy(TestPaths.FixtureFile("claude-desktop", "malformed-json", "input.json"), mConfigPath, overwrite: true);
        string before = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);

        var writer = new ClaudeDesktopWriter(mConfigPath);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnregisterRemovesSaddleRagOnly()
    {
        File.Copy(TestPaths.FixtureFile("claude-desktop", "other-servers-only", "input.json"), mConfigPath, overwrite: true);
        var writer = new ClaudeDesktopWriter(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        var result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains("filesystem", await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("saddlerag", await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnregisterMissingFileIsNoOp()
    {
        var writer = new ClaudeDesktopWriter(mConfigPath);

        var result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.WasNoOp);
    }

    private static string NormalizeJson(string raw)
    {
        return raw.Replace("\r\n", "\n").Trim();
    }
}
