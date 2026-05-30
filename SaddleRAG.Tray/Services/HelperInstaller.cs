// HelperInstaller.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tray.Services;

public sealed class HelperInstaller
{
    public async Task<string> RegisterAsync(HelperClient client, CancellationToken ct)
    {
        IReadOnlyList<IClientWriter> writers = WritersFor(client);
        ClientRegistrar registrar = new(writers);
        RegistrarResult result = await registrar.RegisterAsync(SaddleRagEndpoint.Default, ct);
        return Summarize(result);
    }

    private static IReadOnlyList<IClientWriter> WritersFor(HelperClient client) => client switch
    {
        HelperClient.ClaudeCode    => [ClaudeCodeWriter.ForCurrentUser()],
        HelperClient.ClaudeDesktop => [ClaudeDesktopWriter.ForCurrentUser()],
        HelperClient.VsCode        => [VsCodeMcpWriter.ForCurrentUser()],
        HelperClient.CopilotCli    => [CopilotCliWriter.ForCurrentUser()],
        HelperClient.Codex         => [CodexWriter.ForCurrentUser()],
        _                          => AllWriters()
    };

    private static IReadOnlyList<IClientWriter> AllWriters() =>
        [
            ClaudeCodeWriter.ForCurrentUser(),
            ClaudeDesktopWriter.ForCurrentUser(),
            VsCodeMcpWriter.ForCurrentUser(),
            CopilotCliWriter.ForCurrentUser(),
            CodexWriter.ForCurrentUser()
        ];

    private static string Summarize(RegistrarResult result)
    {
        IEnumerable<string> lines = result.RegisterResults
            .Select(r => $"{(r.Success ? "OK " : "ERR")} {r.ClientName}: {r.Message}");
        return string.Join(Environment.NewLine, lines);
    }
}
