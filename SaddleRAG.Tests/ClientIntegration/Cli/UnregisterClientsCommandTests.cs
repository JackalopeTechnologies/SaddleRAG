// UnregisterClientsCommandTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.CommandLine;
using SaddleRAG.Cli.Commands;

#endregion

namespace SaddleRAG.Tests.ClientIntegration.Cli;

public sealed class UnregisterClientsCommandTests
{
    [Fact]
    public void BuildExposesAllExpectedOptions()
    {
        Command cmd = UnregisterClientsCommand.Build();

        IReadOnlyCollection<string> expected =
            [
                "--claude-code",
            "--claude-desktop",
            "--vscode-mcp",
            "--copilot-cli",
            "--codex",
            "--cursor",
            "--gemini-cli",
            "--windsurf",
            "--visual-studio",
            "--detected-only",
            "--quiet",
            "--log-file"
            ];

        foreach (string opt in expected)
            Assert.Contains(cmd.Options, o => o.Name == opt || o.Aliases.Contains(opt));
    }

    [Fact]
    public void CommandNameIsUnregisterClients()
    {
        Command cmd = UnregisterClientsCommand.Build();
        Assert.Equal("unregister-clients", cmd.Name);
    }
}
