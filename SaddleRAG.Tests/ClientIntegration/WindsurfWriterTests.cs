// WindsurfWriterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json;
using System.Text.Json.Nodes;
using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class WindsurfWriterTests : IDisposable
{
    private const string CodeiumDir = ".codeium";
    private const string WindsurfDir = "windsurf";
    private const string ConfigFile = "mcp_config.json";

    private readonly string mTempDir;
    private readonly string mConfigPath;

    public WindsurfWriterTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), "saddlerag-windsurf-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mTempDir);
        mConfigPath = Path.Combine(mTempDir, CodeiumDir, WindsurfDir, ConfigFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
            Directory.Delete(mTempDir, recursive: true);
    }

    [Fact]
    public async Task RegisterWritesBareServerUrlEntryWithoutTypeOrUrl()
    {
        WindsurfWriter writer = new(mConfigPath);

        RegisterResult result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        JsonObject root = await ReadRootAsync();
        JsonObject entry = EntryOf(root);
        Assert.Equal(SaddleRagEndpoint.Default.Url, entry["serverUrl"]?.GetValue<string>());
        Assert.False(entry.ContainsKey("url"));
        Assert.False(entry.ContainsKey("httpUrl"));
        Assert.False(entry.ContainsKey("type"));
    }

    [Fact]
    public async Task RegisterPreservesUnrelatedServerEntry()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(
            mConfigPath,
            "{\"mcpServers\":{\"other\":{\"serverUrl\":\"http://localhost:9999/mcp\"}}}",
            TestContext.Current.CancellationToken);

        WindsurfWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        JsonObject root = await ReadRootAsync();
        JsonObject servers = ServersOf(root);
        Assert.True(servers.ContainsKey("other"));
        Assert.Equal("http://localhost:9999/mcp", (servers["other"] as JsonObject)?["serverUrl"]?.GetValue<string>());
        Assert.Equal(SaddleRagEndpoint.Default.Url, EntryOf(root)["serverUrl"]?.GetValue<string>());
    }

    [Fact]
    public async Task RegisterTwiceIsIdempotent()
    {
        WindsurfWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        JsonObject servers = ServersOf(await ReadRootAsync());
        Assert.Single(servers);
        Assert.Equal(SaddleRagEndpoint.Default.Url, (servers["saddlerag"] as JsonObject)?["serverUrl"]?.GetValue<string>());
    }

    [Fact]
    public async Task UnregisterRemovesSaddleRagOnly()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(
            mConfigPath,
            "{\"mcpServers\":{\"other\":{\"serverUrl\":\"http://localhost:9999/mcp\"}}}",
            TestContext.Current.CancellationToken);
        WindsurfWriter writer = new(mConfigPath);
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
        WindsurfWriter writer = new(mConfigPath);

        UnregisterResult result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.WasNoOp);
    }

    [Fact]
    public async Task RegisterLeavesNoTmpFileBehind()
    {
        WindsurfWriter writer = new(mConfigPath);

        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(mConfigPath + ".tmp"));
    }

    [Fact]
    public async Task RegisterCreatesNestedDirectoryWhenAbsent()
    {
        WindsurfWriter writer = new(mConfigPath);

        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(mConfigPath));
    }

    [Fact]
    public async Task StatusReportsPresentAndMatchingAfterRegister()
    {
        WindsurfWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        StatusResult status = await writer.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.True(status.ConfigFileExists);
        Assert.True(status.SaddleRagEntryPresent);
        Assert.True(status.EndpointMatchesCanonical);
    }

    [Fact]
    public void IsDetectedTrueWhenWindsurfDirExists()
    {
        string windsurfDir = Path.Combine(mTempDir, CodeiumDir, WindsurfDir);
        Directory.CreateDirectory(windsurfDir);
        WindsurfWriter writer = new(Path.Combine(windsurfDir, ConfigFile));

        Assert.True(writer.IsDetected());
    }

    [Fact]
    public void IsDetectedFalseWhenWindsurfDirMissing()
    {
        WindsurfWriter writer = new(Path.Combine(mTempDir, CodeiumDir, WindsurfDir, ConfigFile));

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
