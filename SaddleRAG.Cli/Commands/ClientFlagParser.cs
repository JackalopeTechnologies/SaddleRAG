// ClientFlagParser.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Cli.Commands;

internal static class ClientFlagParser
{
    public static IEnumerable<IClientWriter> SelectWritersForCurrentUser(
        bool claudeCode,
        bool claudeDesktop,
        bool vscodeMcp,
        bool copilotCli)
    {
        if (claudeCode)
            yield return ClaudeCodeWriter.ForCurrentUser();

        if (claudeDesktop)
            yield return ClaudeDesktopWriter.ForCurrentUser();

        if (vscodeMcp)
            yield return VsCodeMcpWriter.ForCurrentUser();

        if (copilotCli)
            yield return CopilotCliWriter.ForCurrentUser();
    }
}
