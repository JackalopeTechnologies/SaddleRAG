// CodexWriterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class CodexWriterTests : IDisposable
{
    private const string CodexDir = ".codex";
    private const string ConfigFile = "config.toml";

    private readonly string mTempDir;
    private readonly string mConfigPath;

    public CodexWriterTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), "saddlerag-codex-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mTempDir);
        mConfigPath = Path.Combine(mTempDir, CodexDir, ConfigFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
            Directory.Delete(mTempDir, recursive: true);
    }

    [Fact]
    public async Task RegisterOnEmptyWritesFeatureFlagAndServer()
    {
        CodexWriter writer = new(mConfigPath);

        RegisterResult result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        string actual = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);
        Assert.Contains("experimental_use_rmcp_client = true", actual);
        Assert.Contains("[mcp_servers.saddlerag]", actual);
        Assert.Contains(SaddleRagEndpoint.Default.Url, actual);
    }

    [Fact]
    public async Task RegisterPreservesUnrelatedConfig()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(
            mConfigPath,
            "model = \"o3\"\n\n[mcp_servers.other]\ncommand = \"foo\"\n",
            TestContext.Current.CancellationToken);

        CodexWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        string actual = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);
        Assert.Contains("model = \"o3\"", actual);
        Assert.Contains("[mcp_servers.other]", actual);
        Assert.Contains("[mcp_servers.saddlerag]", actual);
    }

    [Fact]
    public async Task UnregisterRemovesSaddleRagServerOnly()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(
            mConfigPath,
            "[mcp_servers.other]\ncommand = \"foo\"\n",
            TestContext.Current.CancellationToken);
        CodexWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        UnregisterResult result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.WasNoOp);
        string actual = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);
        Assert.Contains("[mcp_servers.other]", actual);
        Assert.DoesNotContain("saddlerag", actual);
        Assert.Contains("experimental_use_rmcp_client", actual);
    }

    [Fact]
    public async Task UnregisterMissingFileIsNoOp()
    {
        CodexWriter writer = new(mConfigPath);

        UnregisterResult result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.WasNoOp);
    }

    [Fact]
    public async Task RegisterMalformedTomlReturnsFailureAndLeavesFileUntouched()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(configDir);
        string before = "this is = = not valid toml [[[";
        await File.WriteAllTextAsync(mConfigPath, before, TestContext.Current.CancellationToken);

        CodexWriter writer = new(mConfigPath);
        RegisterResult result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StatusReportsPresentAndMatchingAfterRegister()
    {
        CodexWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        StatusResult status = await writer.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.True(status.ConfigFileExists);
        Assert.True(status.SaddleRagEntryPresent);
        Assert.True(status.EndpointMatchesCanonical);
    }
}
