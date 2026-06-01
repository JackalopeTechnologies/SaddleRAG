// EndToEndTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json.Nodes;
using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class EndToEndTests : IDisposable
{
    private readonly string mFakeProfile;
    private readonly string mClaudeCodeConfig;
    private readonly string mClaudeCodeSkillsBaseDir;
    private readonly string mClaudeCodeFirstSkill;
    private readonly string mClaudeDesktopConfig;
    private readonly string mVsCodeMcp;
    private readonly string mVsCodeSettings;
    private readonly string mVsCodePluginRoot;
    private readonly string mVsCodePluginManifest;
    private readonly string mCopilotCliConfig;
    private readonly string mCopilotCliSkillsBaseDir;
    private readonly string mCopilotCliFirstSkill;

    public EndToEndTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "saddlerag-e2e-" + Guid.NewGuid().ToString("N"));
        mFakeProfile = Path.Combine(root, "profile");
        string fakeAppData = Path.Combine(root, "appdata");
        Directory.CreateDirectory(mFakeProfile);
        Directory.CreateDirectory(fakeAppData);

        mClaudeCodeConfig        = Path.Combine(mFakeProfile, ".claude.json");
        mClaudeCodeSkillsBaseDir = Path.Combine(mFakeProfile, ".claude", "skills");
        mClaudeCodeFirstSkill    = Path.Combine(mClaudeCodeSkillsBaseDir, "saddlerag-first", "SKILL.md");
        mClaudeDesktopConfig     = Path.Combine(fakeAppData, "Claude", "claude_desktop_config.json");
        mVsCodeMcp               = Path.Combine(fakeAppData, "Code", "User", "mcp.json");
        mVsCodeSettings          = Path.Combine(fakeAppData, "Code", "User", "settings.json");
        mVsCodePluginRoot        = Path.Combine(fakeAppData, "Code", "User", "saddlerag-plugin");
        mVsCodePluginManifest    = Path.Combine(mVsCodePluginRoot, "plugin.json");
        mCopilotCliConfig        = Path.Combine(mFakeProfile, ".copilot", "mcp-config.json");
        mCopilotCliSkillsBaseDir = Path.Combine(mFakeProfile, ".copilot", "skills");
        mCopilotCliFirstSkill    = Path.Combine(mCopilotCliSkillsBaseDir, "saddlerag-first", "SKILL.md");
    }

    public void Dispose()
    {
        string? root = Path.GetDirectoryName(mFakeProfile);
        if (root is not null && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task RegisterAllThenUnregisterAllLeavesEverythingClean()
    {
        var writers = new IClientWriter[]
        {
            new ClaudeCodeWriter(mClaudeCodeConfig, mClaudeCodeSkillsBaseDir),
            new ClaudeDesktopWriter(mClaudeDesktopConfig),
            new VsCodeMcpWriter(mVsCodeMcp),
            new CopilotCliWriter(mCopilotCliConfig, mCopilotCliSkillsBaseDir)
        };
        var registrar = new ClientRegistrar(writers);

        var registerResult = await registrar.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);
        Assert.True(registerResult.AllRegisterSucceeded,
            string.Join("; ", registerResult.RegisterResults.Where(r => !r.Success).Select(r => r.Message)));

        Assert.Contains("saddlerag", await File.ReadAllTextAsync(mClaudeCodeConfig, TestContext.Current.CancellationToken));
        Assert.Contains("saddlerag", await File.ReadAllTextAsync(mClaudeDesktopConfig, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(mVsCodePluginManifest));
        JsonObject vsCodeSettings = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(mVsCodeSettings, TestContext.Current.CancellationToken)));
        JsonObject pluginLocations = Assert.IsType<JsonObject>(vsCodeSettings["chat.pluginLocations"]);
        Assert.True(pluginLocations[mVsCodePluginRoot]?.GetValue<bool>());
        Assert.Contains("saddlerag", await File.ReadAllTextAsync(mCopilotCliConfig, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(mClaudeCodeFirstSkill));
        Assert.True(File.Exists(mCopilotCliFirstSkill));

        var unregisterResult = await registrar.UnregisterAsync(TestContext.Current.CancellationToken);
        Assert.True(unregisterResult.AllUnregisterSucceeded);

        if (File.Exists(mClaudeCodeConfig))
            Assert.DoesNotContain("saddlerag", await File.ReadAllTextAsync(mClaudeCodeConfig, TestContext.Current.CancellationToken));
        if (File.Exists(mClaudeDesktopConfig))
            Assert.DoesNotContain("saddlerag", await File.ReadAllTextAsync(mClaudeDesktopConfig, TestContext.Current.CancellationToken));
        if (File.Exists(mVsCodeMcp))
            Assert.DoesNotContain("saddlerag", await File.ReadAllTextAsync(mVsCodeMcp, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(mVsCodePluginRoot));
        if (File.Exists(mCopilotCliConfig))
            Assert.DoesNotContain("saddlerag", await File.ReadAllTextAsync(mCopilotCliConfig, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(mClaudeCodeFirstSkill));
        Assert.False(File.Exists(mCopilotCliFirstSkill));
    }
}
