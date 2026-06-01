// ClientFlagParser.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

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
        bool copilotCli,
        bool codex,
        bool cursor,
        bool gemini,
        bool windsurf,
        bool visualStudio)
    {
        if (claudeCode)
            yield return ClaudeCodeWriter.ForCurrentUser();

        if (claudeDesktop)
            yield return ClaudeDesktopWriter.ForCurrentUser();

        if (vscodeMcp)
            yield return VsCodeMcpWriter.ForCurrentUser();

        if (copilotCli)
            yield return CopilotCliWriter.ForCurrentUser();

        if (codex)
            yield return CodexWriter.ForCurrentUser();

        if (cursor)
            yield return CursorWriter.ForCurrentUser();

        if (gemini)
            yield return GeminiCliWriter.ForCurrentUser();

        if (windsurf)
            yield return WindsurfWriter.ForCurrentUser();

        if (visualStudio)
            yield return VisualStudio2022Writer.ForCurrentUser();
    }
}
