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
                                                                    copilotCli: false
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
                                                                    copilotCli: false
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
                                                                    copilotCli: false
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
                                                                    copilotCli: false
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
                                                                    copilotCli: true
                                                                   )
                                       .ToList();
        Assert.Single(writers);
        Assert.IsType<CopilotCliWriter>(writers[0]);
    }

    [Fact]
    public void AllFlagsReturnFourWritersInRegistrationOrder()
    {
        var writers = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: true,
                                                                    claudeDesktop: true,
                                                                    vscodeMcp: true,
                                                                    copilotCli: true
                                                                   )
                                       .ToList();
        Assert.Collection(writers,
                          w => Assert.IsType<ClaudeCodeWriter>(w),
                          w => Assert.IsType<ClaudeDesktopWriter>(w),
                          w => Assert.IsType<VsCodeMcpWriter>(w),
                          w => Assert.IsType<CopilotCliWriter>(w)
                         );
    }

    [Fact]
    public void NonContiguousFlagsReturnWritersInRegistrationOrder()
    {
        // claudeCode + vscodeMcp set, others not — verifies the parser
        // doesn't reorder the output based on which subset was selected;
        // ClaudeCodeWriter comes before VsCodeMcpWriter because that's
        // the declaration order in SelectWritersForCurrentUser.
        var writers = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: true,
                                                                    claudeDesktop: false,
                                                                    vscodeMcp: true,
                                                                    copilotCli: false
                                                                   )
                                       .ToList();
        Assert.Collection(writers,
                          w => Assert.IsType<ClaudeCodeWriter>(w),
                          w => Assert.IsType<VsCodeMcpWriter>(w)
                         );
    }
}
