// ClientWriterCatalog.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.ClientIntegration;

/// <summary>
///     The single source of truth for the set of supported agents, shared by the CLI and
///     the tray so both stay in lock-step. Add a new agent here once and it appears in the
///     register/unregister/status flows and the tray menus automatically.
/// </summary>
public static class ClientWriterCatalog
{
    public sealed record ClientDescriptor(string Key, string DisplayName, Func<IClientWriter> Factory);

    private static readonly IReadOnlyList<ClientDescriptor> smAll =
    [
        new ClientDescriptor(ClaudeCodeKey, ClaudeCodeName, ClaudeCodeWriter.ForCurrentUser),
        new ClientDescriptor(ClaudeDesktopKey, ClaudeDesktopName, ClaudeDesktopWriter.ForCurrentUser),
        new ClientDescriptor(VsCodeKey, VsCodeName, VsCodeMcpWriter.ForCurrentUser),
        new ClientDescriptor(CopilotKey, CopilotName, CopilotCliWriter.ForCurrentUser),
        new ClientDescriptor(CodexKey, CodexName, CodexWriter.ForCurrentUser),
        new ClientDescriptor(CursorKey, CursorName, CursorWriter.ForCurrentUser),
        new ClientDescriptor(GeminiKey, GeminiName, GeminiCliWriter.ForCurrentUser),
        new ClientDescriptor(WindsurfKey, WindsurfName, WindsurfWriter.ForCurrentUser),
        new ClientDescriptor(VisualStudioKey, VisualStudioName, VisualStudio2022Writer.ForCurrentUser)
    ];

    public static IReadOnlyList<ClientDescriptor> All => smAll;

    public static IClientWriter? FindByKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ClientDescriptor? descriptor =
            smAll.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
        return descriptor?.Factory();
    }

    private const string ClaudeCodeKey = "claude-code";
    private const string ClaudeCodeName = "Claude Code";
    private const string ClaudeDesktopKey = "claude-desktop";
    private const string ClaudeDesktopName = "Claude Desktop";
    private const string VsCodeKey = "vscode-mcp";
    private const string VsCodeName = "VS Code";
    private const string CopilotKey = "copilot-cli";
    private const string CopilotName = "Copilot CLI";
    private const string CodexKey = "codex";
    private const string CodexName = "Codex";
    private const string CursorKey = "cursor";
    private const string CursorName = "Cursor";
    private const string GeminiKey = "gemini-cli";
    private const string GeminiName = "Gemini CLI";
    private const string WindsurfKey = "windsurf";
    private const string WindsurfName = "Windsurf";
    private const string VisualStudioKey = "visual-studio";
    private const string VisualStudioName = "Visual Studio 2022";
}
