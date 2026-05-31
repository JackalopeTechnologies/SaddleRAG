// StatusCommandTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.CommandLine;
using SaddleRAG.Cli.Commands;

#endregion

namespace SaddleRAG.Tests.ClientIntegration.Cli;

public sealed class StatusCommandTests
{
    [Fact]
    public void CommandNameIsClientsStatus()
    {
        Command cmd = StatusCommand.Build();
        Assert.Equal("clients-status", cmd.Name);
    }

    [Fact]
    public void HasJsonOption()
    {
        Command cmd = StatusCommand.Build();
        Assert.Contains(cmd.Options, o => o.Name == "--json");
    }

    [Fact]
    public void HasLogFileOption()
    {
        Command cmd = StatusCommand.Build();
        Assert.Contains(cmd.Options, o => o.Name == "--log-file");
    }

    [Fact]
    public void ExposesAllClientOptions()
    {
        Command cmd = StatusCommand.Build();
        var optionNames = cmd.Options.Select(o => o.Name).ToList();
        Assert.Contains("--claude-code", optionNames);
        Assert.Contains("--claude-desktop", optionNames);
        Assert.Contains("--vscode-mcp", optionNames);
        Assert.Contains("--copilot-cli", optionNames);
        Assert.Contains("--codex", optionNames);
        Assert.Contains("--cursor", optionNames);
        Assert.Contains("--gemini-cli", optionNames);
        Assert.Contains("--windsurf", optionNames);
        Assert.Contains("--visual-studio", optionNames);
    }

    [Fact]
    public void DoesNotExposeDetectedOnlyOption()
    {
        Command cmd = StatusCommand.Build();
        var optionNames = cmd.Options.Select(o => o.Name).ToList();
        Assert.DoesNotContain("--detected-only", optionNames);
    }
}
