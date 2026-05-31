// ClientWriterDetectionTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class ClientWriterDetectionTests : IDisposable
{
    private readonly string mTempDir;

    public ClientWriterDetectionTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), "saddlerag-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mTempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
            Directory.Delete(mTempDir, recursive: true);
    }

    [Fact]
    public void ClaudeCodeDetectedWhenConfigFileExists()
    {
        string configPath = Path.Combine(mTempDir, ".claude.json");
        string skillsBase = Path.Combine(mTempDir, "absent-claude", "skills");
        File.WriteAllText(configPath, "{}");
        var writer = new ClaudeCodeWriter(configPath, skillsBase);

        Assert.True(writer.IsDetected());
    }

    [Fact]
    public void ClaudeCodeDetectedWhenClaudeDirExists()
    {
        string configPath = Path.Combine(mTempDir, "absent.claude.json");
        string claudeDir = Path.Combine(mTempDir, ".claude");
        string skillsBase = Path.Combine(claudeDir, "skills");
        Directory.CreateDirectory(claudeDir);
        var writer = new ClaudeCodeWriter(configPath, skillsBase);

        Assert.True(writer.IsDetected());
    }

    [Fact]
    public void ClaudeCodeNotDetectedWhenNeitherExists()
    {
        string configPath = Path.Combine(mTempDir, "absent.claude.json");
        string skillsBase = Path.Combine(mTempDir, "absent-claude", "skills");
        var writer = new ClaudeCodeWriter(configPath, skillsBase);

        Assert.False(writer.IsDetected());
    }

    [Fact]
    public void ClaudeDesktopDetectedWhenClaudeDirExists()
    {
        string claudeDir = Path.Combine(mTempDir, "Claude");
        Directory.CreateDirectory(claudeDir);
        string configPath = Path.Combine(claudeDir, "claude_desktop_config.json");
        var writer = new ClaudeDesktopWriter(configPath);

        Assert.True(writer.IsDetected());
    }

    [Fact]
    public void ClaudeDesktopNotDetectedWhenClaudeDirMissing()
    {
        string configPath = Path.Combine(mTempDir, "Claude", "claude_desktop_config.json");
        var writer = new ClaudeDesktopWriter(configPath);

        Assert.False(writer.IsDetected());
    }

    [Fact]
    public void VsCodeDetectedWhenCodeDirExists()
    {
        string userDir = Path.Combine(mTempDir, "Code", "User");
        Directory.CreateDirectory(userDir);
        string configPath = Path.Combine(userDir, "mcp.json");
        var writer = new VsCodeMcpWriter(configPath);

        Assert.True(writer.IsDetected());
    }

    [Fact]
    public void VsCodeNotDetectedWhenCodeDirMissing()
    {
        string configPath = Path.Combine(mTempDir, "Code", "User", "mcp.json");
        var writer = new VsCodeMcpWriter(configPath);

        Assert.False(writer.IsDetected());
    }

    [Fact]
    public void CopilotDetectedWhenCopilotDirExists()
    {
        string copilotDir = Path.Combine(mTempDir, ".copilot");
        Directory.CreateDirectory(copilotDir);
        string configPath = Path.Combine(copilotDir, "mcp-config.json");
        string skillsBase = Path.Combine(copilotDir, "skills");
        var writer = new CopilotCliWriter(configPath, skillsBase);

        Assert.True(writer.IsDetected());
    }

    [Fact]
    public void CopilotNotDetectedWhenCopilotDirMissing()
    {
        string copilotDir = Path.Combine(mTempDir, ".copilot");
        string configPath = Path.Combine(copilotDir, "mcp-config.json");
        string skillsBase = Path.Combine(copilotDir, "skills");
        var writer = new CopilotCliWriter(configPath, skillsBase);

        Assert.False(writer.IsDetected());
    }

    [Fact]
    public void CodexDetectedWhenCodexDirExists()
    {
        string codexDir = Path.Combine(mTempDir, ".codex");
        Directory.CreateDirectory(codexDir);
        string configPath = Path.Combine(codexDir, "config.toml");
        var writer = new CodexWriter(configPath);

        Assert.True(writer.IsDetected());
    }

    [Fact]
    public void CodexNotDetectedWhenCodexDirMissing()
    {
        string configPath = Path.Combine(mTempDir, ".codex", "config.toml");
        var writer = new CodexWriter(configPath);

        Assert.False(writer.IsDetected());
    }
}
