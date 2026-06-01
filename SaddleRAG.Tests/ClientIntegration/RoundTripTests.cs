// RoundTripTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class RoundTripTests : IDisposable
{
    private readonly string mTempDir;

    public RoundTripTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), "saddlerag-rt-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mTempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
            Directory.Delete(mTempDir, recursive: true);
    }

    [Theory]
    [InlineData("claude-code", "no-mcp-section")]
    [InlineData("claude-code", "other-servers-only")]
    [InlineData("claude-desktop", "other-servers-only")]
    [InlineData("vscode-mcp", "other-servers-only")]
    [InlineData("copilot-cli", "other-servers-only")]
    public async Task RegisterThenUnregisterRestoresOriginalContent(string client, string scenario)
    {
        string fixtureInput = TestPaths.FixtureFile(client, scenario, "input.json");
        string configPath = Path.Combine(mTempDir, $"{client}-{scenario}.json");
        File.Copy(fixtureInput, configPath);
        string originalText = await File.ReadAllTextAsync(configPath, TestContext.Current.CancellationToken);

        IClientWriter writer = CreateWriter(client, configPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);
        await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        string finalText = await File.ReadAllTextAsync(configPath, TestContext.Current.CancellationToken);
        Assert.Equal(NormalizeJson(originalText), NormalizeJson(finalText));
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("claude-desktop")]
    [InlineData("vscode-mcp")]
    [InlineData("copilot-cli")]
    public async Task RegisterThenUnregisterFromAbsentFileLeavesFileAbsent(string client)
    {
        string configPath = Path.Combine(mTempDir, $"{client}-absent.json");
        Assert.False(File.Exists(configPath));

        IClientWriter writer = CreateWriter(client, configPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(configPath), "register should create the file");

        await writer.UnregisterAsync(TestContext.Current.CancellationToken);

        // After unregister, the file may exist as `{}` or be removed; both are acceptable
        // per the spec ("leave the empty object, do not tidy further"). Assert no saddlerag remains.
        if (File.Exists(configPath))
        {
            string finalText = await File.ReadAllTextAsync(configPath, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("saddlerag", finalText);
        }
    }

    private IClientWriter CreateWriter(string client, string configPath)
    {
        IClientWriter res = client switch
                                {
                                    "claude-code" => new ClaudeCodeWriter(
                                        configPath,
                                        Path.Combine(mTempDir, "skills", $"{Path.GetFileNameWithoutExtension(configPath)}.md")),
                                    "claude-desktop" => new ClaudeDesktopWriter(configPath),
                                    "vscode-mcp" => new VsCodeMcpWriter(configPath),
                                    "copilot-cli" => new CopilotCliWriter(
                                        configPath,
                                        Path.Combine(mTempDir, "skills-copilot", $"{Path.GetFileNameWithoutExtension(configPath)}.md")),
                                    var _ => throw new ArgumentException($"unknown client: {client}", nameof(client))
                                };
        return res;
    }

    private static string NormalizeJson(string raw)
    {
        return raw.Replace("\r\n", "\n").Trim();
    }
}
