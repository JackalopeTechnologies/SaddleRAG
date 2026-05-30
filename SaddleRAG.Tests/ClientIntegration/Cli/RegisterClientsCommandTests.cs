// RegisterClientsCommandTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.CommandLine;
using SaddleRAG.Cli.Commands;

#endregion

namespace SaddleRAG.Tests.ClientIntegration.Cli;

public sealed class RegisterClientsCommandTests
{
    [Fact]
    public void BuildExposesAllExpectedOptions()
    {
        Command cmd = RegisterClientsCommand.Build();

        IReadOnlyCollection<string> expected =
            [
                "--claude-code",
            "--claude-desktop",
            "--vscode-mcp",
            "--copilot-cli",
            "--codex",
            "--quiet",
            "--log-file"
            ];

        foreach (string opt in expected)
            Assert.Contains(cmd.Options, o => o.Name == opt || o.Aliases.Contains(opt));
    }

    [Fact]
    public void CommandNameIsRegisterClients()
    {
        Command cmd = RegisterClientsCommand.Build();
        Assert.Equal("register-clients", cmd.Name);
    }
}
