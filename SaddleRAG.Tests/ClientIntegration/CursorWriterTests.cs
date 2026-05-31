// CursorWriterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Text.Json;
using System.Text.Json.Nodes;
using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class CursorWriterTests : IDisposable
{
    private const string CursorDir = ".cursor";
    private const string ConfigFile = "mcp.json";

    private readonly string mTempDir;
    private readonly string mConfigPath;

    public CursorWriterTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), "saddlerag-cursor-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mTempDir);
        mConfigPath = Path.Combine(mTempDir, CursorDir, ConfigFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
            Directory.Delete(mTempDir, recursive: true);
    }

    [Fact]
    public async Task RegisterWritesBareUrlEntryWithoutType()
    {
        CursorWriter writer = new(mConfigPath);

        RegisterResult result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        JsonObject root = await ReadRootAsync();
        JsonObject entry = EntryOf(root);
        Assert.Equal(SaddleRagEndpoint.Default.Url, entry["url"]?.GetValue<string>());
        Assert.False(entry.ContainsKey("type"));
    }

    [Fact]
    public async Task RegisterPreservesUnrelatedServerEntry()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(
            mConfigPath,
            "{\"mcpServers\":{\"other\":{\"url\":\"http://localhost:9999/mcp\"}}}",
            TestContext.Current.CancellationToken);

        CursorWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        JsonObject root = await ReadRootAsync();
        JsonObject servers = ServersOf(root);
        Assert.True(servers.ContainsKey("other"));
        Assert.Equal("http://localhost:9999/mcp", (servers["other"] as JsonObject)?["url"]?.GetValue<string>());
        Assert.Equal(SaddleRagEndpoint.Default.Url, EntryOf(root)["url"]?.GetValue<string>());
    }

    [Fact]
    public async Task RegisterTwiceIsIdempotent()
    {
        CursorWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        JsonObject servers = ServersOf(await ReadRootAsync());
        Assert.Single(servers);
        Assert.Equal(SaddleRagEndpoint.Default.Url, (servers["saddlerag"] as JsonObject)?["url"]?.GetValue<string>());
    }

    [Fact]
    public async Task UnregisterRemovesSaddleRagOnly()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(
            mConfigPath,
            "{\"mcpServers\":{\"other\":{\"url\":\"http://localhost:9999/mcp\"}}}",
            TestContext.Current.CancellationToken);
        CursorWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        UnregisterResult result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.WasNoOp);
        JsonObject servers = ServersOf(await ReadRootAsync());
        Assert.True(servers.ContainsKey("other"));
        Assert.False(servers.ContainsKey("saddlerag"));
    }

    [Fact]
    public async Task UnregisterMissingFileIsNoOp()
    {
        CursorWriter writer = new(mConfigPath);

        UnregisterResult result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.WasNoOp);
    }

    [Fact]
    public async Task RegisterLeavesNoTmpFileBehind()
    {
        CursorWriter writer = new(mConfigPath);

        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(mConfigPath + ".tmp"));
    }

    [Fact]
    public async Task StatusReportsPresentAndMatchingAfterRegister()
    {
        CursorWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        StatusResult status = await writer.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.True(status.ConfigFileExists);
        Assert.True(status.SaddleRagEntryPresent);
        Assert.True(status.EndpointMatchesCanonical);
    }

    [Fact]
    public void IsDetectedTrueWhenCursorDirExists()
    {
        string cursorDir = Path.Combine(mTempDir, CursorDir);
        Directory.CreateDirectory(cursorDir);
        CursorWriter writer = new(Path.Combine(cursorDir, ConfigFile));

        Assert.True(writer.IsDetected());
    }

    [Fact]
    public void IsDetectedFalseWhenCursorDirMissing()
    {
        CursorWriter writer = new(Path.Combine(mTempDir, CursorDir, ConfigFile));

        Assert.False(writer.IsDetected());
    }

    private async Task<JsonObject> ReadRootAsync()
    {
        string text = await File.ReadAllTextAsync(mConfigPath, TestContext.Current.CancellationToken);
        JsonObject root = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("root is not an object");
        return root;
    }

    private static JsonObject ServersOf(JsonObject root)
    {
        JsonObject servers = root["mcpServers"] as JsonObject ?? throw new JsonException("mcpServers missing");
        return servers;
    }

    private static JsonObject EntryOf(JsonObject root)
    {
        JsonObject entry = ServersOf(root)["saddlerag"] as JsonObject ?? throw new JsonException("saddlerag entry missing");
        return entry;
    }
}
