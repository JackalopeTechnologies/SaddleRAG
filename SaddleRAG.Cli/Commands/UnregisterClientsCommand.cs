// UnregisterClientsCommand.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.CommandLine;
using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.Cli.Commands;

public static class UnregisterClientsCommand
{
    private const string CommandName = "unregister-clients";
    private const string CommandDescription = "Remove SaddleRAG from supported AI tools' per-user config";
    private const string ClaudeCodeOptionName = "--claude-code";
    private const string ClaudeCodeOptionDescription = "Unregister from Claude Code";
    private const string ClaudeDesktopOptionName = "--claude-desktop";
    private const string ClaudeDesktopOptionDescription = "Unregister from Claude Desktop";
    private const string VscodeMcpOptionName = "--vscode-mcp";
    private const string VscodeMcpOptionDescription = "Unregister from VSCode native MCP";
    private const string CopilotCliOptionName = "--copilot-cli";
    private const string CopilotCliOptionDescription = "Unregister from Copilot CLI";
    private const string QuietOptionName = "--quiet";
    private const string QuietOptionDescription = "Suppress per-writer stdout lines";
    private const string LogFileOptionName = "--log-file";
    private const string LogFileOptionDescription = "Append per-writer results to this file";
    private const string ResultLineFormat = "{0,-16} {1} {2} — {3}";
    private const string OkLabel = "OK  ";
    private const string NoopLabel = "NOOP";
    private const string ErrLabel = "ERR ";
    private const int ExitCodeAllOk = 0;
    private const int ExitCodeSomeFailed = 2;

    private static readonly Option<bool> smClaudeCode    = new(ClaudeCodeOptionName)    { Description = ClaudeCodeOptionDescription,    DefaultValueFactory = _ => true };
    private static readonly Option<bool> smClaudeDesktop = new(ClaudeDesktopOptionName) { Description = ClaudeDesktopOptionDescription, DefaultValueFactory = _ => true };
    private static readonly Option<bool> smVscodeMcp     = new(VscodeMcpOptionName)     { Description = VscodeMcpOptionDescription,     DefaultValueFactory = _ => true };
    private static readonly Option<bool> smCopilotCli    = new(CopilotCliOptionName)    { Description = CopilotCliOptionDescription,    DefaultValueFactory = _ => true };
    private static readonly Option<bool> smQuiet         = new(QuietOptionName)         { Description = QuietOptionDescription,         DefaultValueFactory = _ => false };
    private static readonly Option<string?> smLogFile    = new(LogFileOptionName)       { Description = LogFileOptionDescription };

    public static Command Build()
    {
        Command cmd = new(CommandName, CommandDescription);
        cmd.Options.Add(smClaudeCode);
        cmd.Options.Add(smClaudeDesktop);
        cmd.Options.Add(smVscodeMcp);
        cmd.Options.Add(smCopilotCli);
        cmd.Options.Add(smQuiet);
        cmd.Options.Add(smLogFile);
        cmd.SetAction(ExecuteAsync);
        return cmd;
    }

    private static async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken ct)
    {
        bool cc     = parseResult.GetValue(smClaudeCode);
        bool cd     = parseResult.GetValue(smClaudeDesktop);
        bool vs     = parseResult.GetValue(smVscodeMcp);
        bool co     = parseResult.GetValue(smCopilotCli);
        bool q      = parseResult.GetValue(smQuiet);
        string? log = parseResult.GetValue(smLogFile);

        var writers   = ClientFlagParser.SelectWritersForCurrentUser(cc, cd, vs, co);
        var registrar = new ClientRegistrar(writers);
        var result    = await registrar.UnregisterAsync(ct);

        int exitCode = result.AllUnregisterSucceeded ? ExitCodeAllOk : ExitCodeSomeFailed;

        foreach (UnregisterResult r in result.UnregisterResults)
        {
            string statusLabel = r.Success ? (r.WasNoOp ? NoopLabel : OkLabel) : ErrLabel;
            string line = string.Format(ResultLineFormat, r.ClientName, statusLabel, r.ConfigPath, r.Message);

            if (!q)
                Console.WriteLine(line);

            if (log is not null)
                await File.AppendAllTextAsync(log, line + Environment.NewLine, ct);
        }

        return exitCode;
    }
}
