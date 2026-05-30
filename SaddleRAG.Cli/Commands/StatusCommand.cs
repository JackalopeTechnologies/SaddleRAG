// StatusCommand.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

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
        cmd.Options.Add(smJson);
        cmd.Options.Add(smLogFile);
        cmd.SetAction(ExecuteAsync);
        return cmd;
    }

    private static async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken ct)
    {
        bool emitJson = parseResult.GetValue(smJson);
        string? logFile = parseResult.GetValue(smLogFile);

        var writers   = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: true, claudeDesktop: true, vscodeMcp: true, copilotCli: true, codex: true);
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
