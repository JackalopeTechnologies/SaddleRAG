// StatusCommand.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.CommandLine;
using System.Text;
using System.Text.Json;
using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.Cli.Commands;

public static class StatusCommand
{
    private const string CommandName = "clients-status";
    private const string CommandDescription = "Show SaddleRAG registration status across all supported AI tools, including VS Code plugin path and enabled state";
    private const string ClaudeCodeOptionName = "--claude-code";
    private const string ClaudeCodeOptionDescription = "Include Claude Code.";
    private const string ClaudeDesktopOptionName = "--claude-desktop";
    private const string ClaudeDesktopOptionDescription = "Include Claude Desktop.";
    private const string VscodeMcpOptionName = "--vscode-mcp";
    private const string VscodeMcpOptionDescription = "Include VS Code.";
    private const string CopilotCliOptionName = "--copilot-cli";
    private const string CopilotCliOptionDescription = "Include GitHub Copilot CLI.";
    private const string CodexOptionName = "--codex";
    private const string CodexOptionDescription = "Include OpenAI Codex CLI.";
    private const string CursorOptionName = "--cursor";
    private const string CursorOptionDescription = "Include Cursor.";
    private const string GeminiCliOptionName = "--gemini-cli";
    private const string GeminiCliOptionDescription = "Include Gemini CLI.";
    private const string WindsurfOptionName = "--windsurf";
    private const string WindsurfOptionDescription = "Include Windsurf.";
    private const string VisualStudioOptionName = "--visual-studio";
    private const string VisualStudioOptionDescription = "Include Visual Studio 2022.";
    private const string JsonOptionName = "--json";
    private const string JsonOptionDescription = "Emit JSON instead of human-readable lines";
    private const string LogFileOptionName = "--log-file";
    private const string LogFileOptionDescription = "Append the rendered status report to this file";
    private const string OkMarker = "OK ";
    private const string OldMarker = "OLD";
    private const string MissingMarker = "—  ";
    private const string ResultLineFormat = "{0,-16} {1} {2}";
    private const string NotesIndent = "                     ";
    private const string PluginPathLabel = "plugin path: ";
    private const string PluginEnabledLabel = "plugin enabled: ";
    private const string AgentPluginsEnabledLabel = "agent plugins enabled: ";
    private const string EnabledState = "true";
    private const string DisabledState = "false";
    private const string UnknownState = "unknown";

    private static readonly Option<bool> smClaudeCode    = new(ClaudeCodeOptionName)    { Description = ClaudeCodeOptionDescription,    DefaultValueFactory = _ => true };
    private static readonly Option<bool> smClaudeDesktop = new(ClaudeDesktopOptionName) { Description = ClaudeDesktopOptionDescription, DefaultValueFactory = _ => true };
    private static readonly Option<bool> smVscodeMcp     = new(VscodeMcpOptionName)     { Description = VscodeMcpOptionDescription,     DefaultValueFactory = _ => true };
    private static readonly Option<bool> smCopilotCli    = new(CopilotCliOptionName)    { Description = CopilotCliOptionDescription,    DefaultValueFactory = _ => true };
    private static readonly Option<bool> smCodex         = new(CodexOptionName)         { Description = CodexOptionDescription,         DefaultValueFactory = _ => true };
    private static readonly Option<bool> smCursor        = new(CursorOptionName)        { Description = CursorOptionDescription,        DefaultValueFactory = _ => true };
    private static readonly Option<bool> smGeminiCli     = new(GeminiCliOptionName)     { Description = GeminiCliOptionDescription,     DefaultValueFactory = _ => true };
    private static readonly Option<bool> smWindsurf      = new(WindsurfOptionName)      { Description = WindsurfOptionDescription,      DefaultValueFactory = _ => true };
    private static readonly Option<bool> smVisualStudio  = new(VisualStudioOptionName)  { Description = VisualStudioOptionDescription,  DefaultValueFactory = _ => true };

    private static readonly Option<bool> smJson = new(JsonOptionName)
    {
        Description = JsonOptionDescription,
        DefaultValueFactory = _ => false
    };

    private static readonly Option<string?> smLogFile = new(LogFileOptionName)
    {
        Description = LogFileOptionDescription
    };

    public static Command Build()
    {
        Command cmd = new(CommandName, CommandDescription);
        cmd.Options.Add(smClaudeCode);
        cmd.Options.Add(smClaudeDesktop);
        cmd.Options.Add(smVscodeMcp);
        cmd.Options.Add(smCopilotCli);
        cmd.Options.Add(smCodex);
        cmd.Options.Add(smCursor);
        cmd.Options.Add(smGeminiCli);
        cmd.Options.Add(smWindsurf);
        cmd.Options.Add(smVisualStudio);
        cmd.Options.Add(smJson);
        cmd.Options.Add(smLogFile);
        cmd.SetAction(ExecuteAsync);
        return cmd;
    }

    private static async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken ct)
    {
        bool cc         = parseResult.GetValue(smClaudeCode);
        bool cd         = parseResult.GetValue(smClaudeDesktop);
        bool vs         = parseResult.GetValue(smVscodeMcp);
        bool co         = parseResult.GetValue(smCopilotCli);
        bool cx         = parseResult.GetValue(smCodex);
        bool cur        = parseResult.GetValue(smCursor);
        bool gem        = parseResult.GetValue(smGeminiCli);
        bool win        = parseResult.GetValue(smWindsurf);
        bool vstudio    = parseResult.GetValue(smVisualStudio);
        bool emitJson   = parseResult.GetValue(smJson);
        string? logFile = parseResult.GetValue(smLogFile);

        var writers   = ClientFlagParser.SelectWritersForCurrentUser(cc, cd, vs, co, cx, cur, gem, win, vstudio);
        var registrar = new ClientRegistrar(writers);
        IReadOnlyList<StatusResult> results = await registrar.GetStatusAsync(ct);
        string output = emitJson ? BuildJsonOutput(results) : BuildTextOutput(results);

        Console.Write(output);

        if (logFile is not null)
            await File.AppendAllTextAsync(logFile, output, ct);

        return 0;
    }

    private static string BuildJsonOutput(IReadOnlyList<StatusResult> results)
    {
        string res = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        return res;
    }

    private static string BuildTextOutput(IReadOnlyList<StatusResult> results)
    {
        var builder = new StringBuilder();
        foreach (StatusResult result in results)
            AppendTextResult(builder, result);
        string res = builder.ToString();
        return res;
    }

    private static void AppendTextResult(StringBuilder builder, StatusResult result)
    {
        string mark = result.SaddleRagEntryPresent ? (result.EndpointMatchesCanonical ? OkMarker : OldMarker) : MissingMarker;
        builder.AppendLine(string.Format(ResultLineFormat, result.ClientName, mark, result.ConfigPath));

        if (!string.IsNullOrWhiteSpace(result.PluginPath))
            builder.Append(NotesIndent).Append(PluginPathLabel).AppendLine(result.PluginPath);

        if (result.PluginEnabled.HasValue)
            builder.Append(NotesIndent).Append(PluginEnabledLabel).AppendLine(FormatEnabledState(result.PluginEnabled));

        if (result.AgentPluginsEnabled.HasValue)
            builder.Append(NotesIndent).Append(AgentPluginsEnabledLabel).AppendLine(FormatEnabledState(result.AgentPluginsEnabled));

        if (!string.IsNullOrEmpty(result.Notes))
            builder.Append(NotesIndent).AppendLine(result.Notes);
    }

    private static string FormatEnabledState(bool? enabled)
    {
        string res = UnknownState;
        if (enabled.HasValue)
            res = enabled.Value ? EnabledState : DisabledState;
        return res;
    }
}
