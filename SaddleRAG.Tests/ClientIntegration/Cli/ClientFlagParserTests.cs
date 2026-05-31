// ClientFlagParserTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Cli.Commands;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration.Cli;

public sealed class ClientFlagParserTests
{
    [Fact]
    public void NoFlagsSelectedReturnsEmpty()
    {
        var writers = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: false,
                                                                    claudeDesktop: false,
                                                                    vscodeMcp: false,
                                                                    copilotCli: false,
                                                                    codex: false,
                                                                    cursor: false,
                                                                    gemini: false,
                                                                    windsurf: false,
                                                                    visualStudio: false
                                                                   )
                                       .ToList();
        Assert.Empty(writers);
    }

    [Fact]
    public void ClaudeCodeOnlyReturnsSingleClaudeCodeWriter()
    {
        var writers = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: true,
                                                                    claudeDesktop: false,
                                                                    vscodeMcp: false,
                                                                    copilotCli: false,
                                                                    codex: false,
                                                                    cursor: false,
                                                                    gemini: false,
                                                                    windsurf: false,
                                                                    visualStudio: false
                                                                   )
                                       .ToList();
        Assert.Single(writers);
        Assert.IsType<ClaudeCodeWriter>(writers[0]);
    }

    [Fact]
    public void ClaudeDesktopOnlyReturnsSingleClaudeDesktopWriter()
    {
        var writers = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: false,
                                                                    claudeDesktop: true,
                                                                    vscodeMcp: false,
                                                                    copilotCli: false,
                                                                    codex: false,
                                                                    cursor: false,
                                                                    gemini: false,
                                                                    windsurf: false,
                                                                    visualStudio: false
                                                                   )
                                       .ToList();
        Assert.Single(writers);
        Assert.IsType<ClaudeDesktopWriter>(writers[0]);
    }

    [Fact]
    public void VsCodeMcpOnlyReturnsSingleVsCodeMcpWriter()
    {
        var writers = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: false,
                                                                    claudeDesktop: false,
                                                                    vscodeMcp: true,
                                                                    copilotCli: false,
                                                                    codex: false,
                                                                    cursor: false,
                                                                    gemini: false,
                                                                    windsurf: false,
                                                                    visualStudio: false
                                                                   )
                                       .ToList();
        Assert.Single(writers);
        Assert.IsType<VsCodeMcpWriter>(writers[0]);
    }

    [Fact]
    public void CopilotCliOnlyReturnsSingleCopilotCliWriter()
    {
        var writers = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: false,
                                                                    claudeDesktop: false,
                                                                    vscodeMcp: false,
                                                                    copilotCli: true,
                                                                    codex: false,
                                                                    cursor: false,
                                                                    gemini: false,
                                                                    windsurf: false,
                                                                    visualStudio: false
                                                                   )
                                       .ToList();
        Assert.Single(writers);
        Assert.IsType<CopilotCliWriter>(writers[0]);
    }

    [Fact]
    public void CodexOnlyReturnsSingleCodexWriter()
    {
        var writers = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: false,
                                                                    claudeDesktop: false,
                                                                    vscodeMcp: false,
                                                                    copilotCli: false,
                                                                    codex: true,
                                                                    cursor: false,
                                                                    gemini: false,
                                                                    windsurf: false,
                                                                    visualStudio: false
                                                                   )
                                       .ToList();
        Assert.Single(writers);
        Assert.IsType<CodexWriter>(writers[0]);
    }

    [Fact]
    public void CursorOnlyReturnsSingleCursorWriter()
    {
        var writers = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: false,
                                                                    claudeDesktop: false,
                                                                    vscodeMcp: false,
                                                                    copilotCli: false,
                                                                    codex: false,
                                                                    cursor: true,
                                                                    gemini: false,
                                                                    windsurf: false,
                                                                    visualStudio: false
                                                                   )
                                       .ToList();
        Assert.Single(writers);
        Assert.IsType<CursorWriter>(writers[0]);
        Assert.Equal("cursor", writers[0].ClientName);
    }

    [Fact]
    public void GeminiCliOnlyReturnsSingleGeminiCliWriter()
    {
        var writers = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: false,
                                                                    claudeDesktop: false,
                                                                    vscodeMcp: false,
                                                                    copilotCli: false,
                                                                    codex: false,
                                                                    cursor: false,
                                                                    gemini: true,
                                                                    windsurf: false,
                                                                    visualStudio: false
                                                                   )
                                       .ToList();
        Assert.Single(writers);
        Assert.IsType<GeminiCliWriter>(writers[0]);
        Assert.Equal("gemini-cli", writers[0].ClientName);
    }

    [Fact]
    public void WindsurfOnlyReturnsSingleWindsurfWriter()
    {
        var writers = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: false,
                                                                    claudeDesktop: false,
                                                                    vscodeMcp: false,
                                                                    copilotCli: false,
                                                                    codex: false,
                                                                    cursor: false,
                                                                    gemini: false,
                                                                    windsurf: true,
                                                                    visualStudio: false
                                                                   )
                                       .ToList();
        Assert.Single(writers);
        Assert.IsType<WindsurfWriter>(writers[0]);
        Assert.Equal("windsurf", writers[0].ClientName);
    }

    [Fact]
    public void VisualStudioOnlyReturnsSingleVisualStudioWriter()
    {
        var writers = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: false,
                                                                    claudeDesktop: false,
                                                                    vscodeMcp: false,
                                                                    copilotCli: false,
                                                                    codex: false,
                                                                    cursor: false,
                                                                    gemini: false,
                                                                    windsurf: false,
                                                                    visualStudio: true
                                                                   )
                                       .ToList();
        Assert.Single(writers);
        Assert.IsType<VisualStudio2022Writer>(writers[0]);
        Assert.Equal("visual-studio", writers[0].ClientName);
    }

    [Fact]
    public void CodexFalseExcludesCodexWriter()
    {
        var writers = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: true,
                                                                    claudeDesktop: true,
                                                                    vscodeMcp: true,
                                                                    copilotCli: true,
                                                                    codex: false,
                                                                    cursor: false,
                                                                    gemini: false,
                                                                    windsurf: false,
                                                                    visualStudio: false
                                                                   )
                                       .ToList();
        Assert.Equal(4, writers.Count);
        Assert.DoesNotContain(writers, w => w is CodexWriter);
    }

    [Fact]
    public void AllFlagsReturnNineWritersInRegistrationOrder()
    {
        var writers = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: true,
                                                                    claudeDesktop: true,
                                                                    vscodeMcp: true,
                                                                    copilotCli: true,
                                                                    codex: true,
                                                                    cursor: true,
                                                                    gemini: true,
                                                                    windsurf: true,
                                                                    visualStudio: true
                                                                   )
                                       .ToList();
        Assert.Collection(writers,
                          w => Assert.IsType<ClaudeCodeWriter>(w),
                          w => Assert.IsType<ClaudeDesktopWriter>(w),
                          w => Assert.IsType<VsCodeMcpWriter>(w),
                          w => Assert.IsType<CopilotCliWriter>(w),
                          w => Assert.IsType<CodexWriter>(w),
                          w => Assert.IsType<CursorWriter>(w),
                          w => Assert.IsType<GeminiCliWriter>(w),
                          w => Assert.IsType<WindsurfWriter>(w),
                          w => Assert.IsType<VisualStudio2022Writer>(w)
                         );
    }

    [Fact]
    public void AllFlagsYieldEachDistinctClientName()
    {
        var names = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: true,
                                                                 claudeDesktop: true,
                                                                 vscodeMcp: true,
                                                                 copilotCli: true,
                                                                 codex: true,
                                                                 cursor: true,
                                                                 gemini: true,
                                                                 windsurf: true,
                                                                 visualStudio: true
                                                                )
                                     .Select(w => w.ClientName)
                                     .ToList();
        Assert.Equal(9, names.Count);
        Assert.Contains("claude-code", names);
        Assert.Contains("claude-desktop", names);
        Assert.Contains("vscode-mcp", names);
        Assert.Contains("copilot-cli", names);
        Assert.Contains("codex", names);
        Assert.Contains("cursor", names);
        Assert.Contains("gemini-cli", names);
        Assert.Contains("windsurf", names);
        Assert.Contains("visual-studio", names);
        Assert.Equal(9, names.Distinct().Count());
    }

    [Fact]
    public void NonContiguousFlagsReturnWritersInRegistrationOrder()
    {
        var writers = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: true,
                                                                    claudeDesktop: false,
                                                                    vscodeMcp: true,
                                                                    copilotCli: false,
                                                                    codex: false,
                                                                    cursor: false,
                                                                    gemini: false,
                                                                    windsurf: false,
                                                                    visualStudio: false
                                                                   )
                                       .ToList();
        Assert.Collection(writers,
                          w => Assert.IsType<ClaudeCodeWriter>(w),
                          w => Assert.IsType<VsCodeMcpWriter>(w)
                         );
    }
}
