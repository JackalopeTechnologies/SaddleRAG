// RegisterClientsCommandTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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

        IReadOnlyCollection<string> expected = new[]
        {
            "--claude-code",
            "--claude-desktop",
            "--vscode-mcp",
            "--copilot-cli",
            "--quiet",
            "--log-file"
        };

        foreach (string opt in expected)
        {
            Assert.Contains(cmd.Options, o => o.Name == opt || o.Aliases.Contains(opt));
        }
    }

    [Fact]
    public void CommandNameIsRegisterClients()
    {
        Command cmd = RegisterClientsCommand.Build();
        Assert.Equal("register-clients", cmd.Name);
    }
}
