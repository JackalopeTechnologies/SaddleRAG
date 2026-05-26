// StatusCommand.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.CommandLine;
using System.Text.Json;
using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.Cli.Commands;

public static class StatusCommand
{
    private const string CommandName = "clients-status";
    private const string CommandDescription = "Show SaddleRAG registration status across all supported AI tools";
    private const string JsonOptionName = "--json";
    private const string JsonOptionDescription = "Emit JSON instead of human-readable lines";
    private const string OkMarker = "OK ";
    private const string OldMarker = "OLD";
    private const string MissingMarker = "—  ";
    private const string ResultLineFormat = "{0,-16} {1} {2}";
    private const string NotesIndent = "                     ";

    private static readonly Option<bool> smJson = new(JsonOptionName)
    {
        Description = JsonOptionDescription,
        DefaultValueFactory = _ => false
    };

    public static Command Build()
    {
        Command cmd = new(CommandName, CommandDescription);
        cmd.Options.Add(smJson);
        cmd.SetAction(ExecuteAsync);
        return cmd;
    }

    private static async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken ct)
    {
        bool emitJson = parseResult.GetValue(smJson);

        var writers   = ClientFlagParser.SelectWritersForCurrentUser(claudeCode: true, claudeDesktop: true, vscodeMcp: true, copilotCli: true);
        var registrar = new ClientRegistrar(writers);
        IReadOnlyList<StatusResult> results = await registrar.GetStatusAsync(ct);

        if (emitJson)
            Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        else
        {
            foreach (StatusResult r in results)
            {
                string mark = r.SaddleRagEntryPresent ? (r.EndpointMatchesCanonical ? OkMarker : OldMarker) : MissingMarker;
                Console.WriteLine(string.Format(ResultLineFormat, r.ClientName, mark, r.ConfigPath));

                if (!string.IsNullOrEmpty(r.Notes))
                    Console.WriteLine(NotesIndent + r.Notes);
            }
        }

        return 0;
    }
}
