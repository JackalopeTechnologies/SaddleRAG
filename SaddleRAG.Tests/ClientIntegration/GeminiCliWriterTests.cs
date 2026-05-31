// GeminiCliWriterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Text.Json;
using System.Text.Json.Nodes;
using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class GeminiCliWriterTests : IDisposable
{
    private const string GeminiDir = ".gemini";
    private const string ConfigFile = "settings.json";

    private readonly string mTempDir;
    private readonly string mConfigPath;

    public GeminiCliWriterTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), "saddlerag-gemini-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mTempDir);
        mConfigPath = Path.Combine(mTempDir, GeminiDir, ConfigFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
            Directory.Delete(mTempDir, recursive: true);
    }

    [Fact]
    public async Task RegisterWritesHttpUrlEntryWithoutOtherUrlKeys()
    {
        GeminiCliWriter writer = new(mConfigPath);

        RegisterResult result = await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        JsonObject root = await ReadRootAsync();
        JsonObject entry = EntryOf(root);
        Assert.Equal(SaddleRagEndpoint.Default.Url, entry["httpUrl"]?.GetValue<string>());
        Assert.False(entry.ContainsKey("url"));
        Assert.False(entry.ContainsKey("type"));
        Assert.False(entry.ContainsKey("serverUrl"));
    }

    [Fact]
    public async Task RegisterPreservesUnrelatedTopLevelKey()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(
            mConfigPath,
            "{\"theme\":\"dark\"}",
            TestContext.Current.CancellationToken);

        GeminiCliWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        JsonObject root = await ReadRootAsync();
        Assert.Equal("dark", root["theme"]?.GetValue<string>());
        Assert.Equal(SaddleRagEndpoint.Default.Url, EntryOf(root)["httpUrl"]?.GetValue<string>());
    }

    [Fact]
    public async Task UnregisterPreservesUnrelatedTopLevelKey()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(
            mConfigPath,
            "{\"theme\":\"dark\"}",
            TestContext.Current.CancellationToken);
        GeminiCliWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        UnregisterResult result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        JsonObject root = await ReadRootAsync();
        Assert.Equal("dark", root["theme"]?.GetValue<string>());
        Assert.False(ServersOf(root).ContainsKey("saddlerag"));
    }

    [Fact]
    public async Task RegisterPreservesUnrelatedServerEntry()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(
            mConfigPath,
            "{\"mcpServers\":{\"other\":{\"httpUrl\":\"http://localhost:9999/mcp\"}}}",
            TestContext.Current.CancellationToken);

        GeminiCliWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        JsonObject root = await ReadRootAsync();
        JsonObject servers = ServersOf(root);
        Assert.True(servers.ContainsKey("other"));
        Assert.Equal("http://localhost:9999/mcp", (servers["other"] as JsonObject)?["httpUrl"]?.GetValue<string>());
        Assert.Equal(SaddleRagEndpoint.Default.Url, EntryOf(root)["httpUrl"]?.GetValue<string>());
    }

    [Fact]
    public async Task RegisterTwiceIsIdempotent()
    {
        GeminiCliWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        JsonObject servers = ServersOf(await ReadRootAsync());
        Assert.Single(servers);
        Assert.Equal(SaddleRagEndpoint.Default.Url, (servers["saddlerag"] as JsonObject)?["httpUrl"]?.GetValue<string>());
    }

    [Fact]
    public async Task UnregisterRemovesSaddleRagOnly()
    {
        string configDir = Path.GetDirectoryName(mConfigPath) ?? mTempDir;
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(
            mConfigPath,
            "{\"theme\":\"dark\",\"mcpServers\":{\"other\":{\"httpUrl\":\"http://localhost:9999/mcp\"}}}",
            TestContext.Current.CancellationToken);
        GeminiCliWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        UnregisterResult result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.WasNoOp);
        JsonObject root = await ReadRootAsync();
        Assert.Equal("dark", root["theme"]?.GetValue<string>());
        JsonObject servers = ServersOf(root);
        Assert.True(servers.ContainsKey("other"));
        Assert.False(servers.ContainsKey("saddlerag"));
    }

    [Fact]
    public async Task UnregisterMissingFileIsNoOp()
    {
        GeminiCliWriter writer = new(mConfigPath);

        UnregisterResult result = await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.WasNoOp);
    }

    [Fact]
    public async Task RegisterLeavesNoTmpFileBehind()
    {
        GeminiCliWriter writer = new(mConfigPath);

        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(mConfigPath + ".tmp"));
    }

    [Fact]
    public async Task StatusReportsPresentAndMatchingAfterRegister()
    {
        GeminiCliWriter writer = new(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        StatusResult status = await writer.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.True(status.ConfigFileExists);
        Assert.True(status.SaddleRagEntryPresent);
        Assert.True(status.EndpointMatchesCanonical);
    }

    [Fact]
    public void IsDetectedTrueWhenGeminiDirExists()
    {
        string geminiDir = Path.Combine(mTempDir, GeminiDir);
        Directory.CreateDirectory(geminiDir);
        GeminiCliWriter writer = new(Path.Combine(geminiDir, ConfigFile));

        Assert.True(writer.IsDetected());
    }

    [Fact]
    public void IsDetectedFalseWhenGeminiDirMissing()
    {
        GeminiCliWriter writer = new(Path.Combine(mTempDir, GeminiDir, ConfigFile));

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
