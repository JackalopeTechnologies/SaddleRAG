// EndToEndTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class EndToEndTests : IDisposable
{
    private readonly string mFakeProfile;
    private readonly string mClaudeCodeConfig;
    private readonly string mClaudeCodeSkill;
    private readonly string mClaudeDesktopConfig;
    private readonly string mVsCodeMcp;
    private readonly string mCopilotCliConfig;
    private readonly string mCopilotCliSkill;

    public EndToEndTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "saddlerag-e2e-" + Guid.NewGuid().ToString("N"));
        mFakeProfile = Path.Combine(root, "profile");
        string fakeAppData = Path.Combine(root, "appdata");
        Directory.CreateDirectory(mFakeProfile);
        Directory.CreateDirectory(fakeAppData);

        mClaudeCodeConfig    = Path.Combine(mFakeProfile, ".claude.json");
        mClaudeCodeSkill     = Path.Combine(mFakeProfile, ".claude", "skills", "saddlerag-first", "SKILL.md");
        mClaudeDesktopConfig = Path.Combine(fakeAppData, "Claude", "claude_desktop_config.json");
        mVsCodeMcp           = Path.Combine(fakeAppData, "Code", "User", "mcp.json");
        mCopilotCliConfig    = Path.Combine(mFakeProfile, ".copilot", "mcp-config.json");
        mCopilotCliSkill     = Path.Combine(mFakeProfile, ".copilot", "skills", "saddlerag-first", "SKILL.md");
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
            new ClaudeCodeWriter(mClaudeCodeConfig, mClaudeCodeSkill),
            new ClaudeDesktopWriter(mClaudeDesktopConfig),
            new VsCodeMcpWriter(mVsCodeMcp),
            new CopilotCliWriter(mCopilotCliConfig, mCopilotCliSkill)
        };
        var registrar = new ClientRegistrar(writers);

        var registerResult = await registrar.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);
        Assert.True(registerResult.AllRegisterSucceeded,
            string.Join("; ", registerResult.RegisterResults.Where(r => !r.Success).Select(r => r.Message)));

        Assert.Contains("saddlerag", await File.ReadAllTextAsync(mClaudeCodeConfig, TestContext.Current.CancellationToken));
        Assert.Contains("saddlerag", await File.ReadAllTextAsync(mClaudeDesktopConfig, TestContext.Current.CancellationToken));
        Assert.Contains("saddlerag", await File.ReadAllTextAsync(mVsCodeMcp, TestContext.Current.CancellationToken));
        Assert.Contains("saddlerag", await File.ReadAllTextAsync(mCopilotCliConfig, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(mClaudeCodeSkill));
        Assert.True(File.Exists(mCopilotCliSkill));

        var unregisterResult = await registrar.UnregisterAsync(TestContext.Current.CancellationToken);
        Assert.True(unregisterResult.AllUnregisterSucceeded);

        if (File.Exists(mClaudeCodeConfig))
            Assert.DoesNotContain("saddlerag", await File.ReadAllTextAsync(mClaudeCodeConfig, TestContext.Current.CancellationToken));
        if (File.Exists(mClaudeDesktopConfig))
            Assert.DoesNotContain("saddlerag", await File.ReadAllTextAsync(mClaudeDesktopConfig, TestContext.Current.CancellationToken));
        if (File.Exists(mVsCodeMcp))
            Assert.DoesNotContain("saddlerag", await File.ReadAllTextAsync(mVsCodeMcp, TestContext.Current.CancellationToken));
        if (File.Exists(mCopilotCliConfig))
            Assert.DoesNotContain("saddlerag", await File.ReadAllTextAsync(mCopilotCliConfig, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(mClaudeCodeSkill));
        Assert.False(File.Exists(mCopilotCliSkill));
    }
}
