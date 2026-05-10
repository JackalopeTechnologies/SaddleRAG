// StatusCommandTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
}
