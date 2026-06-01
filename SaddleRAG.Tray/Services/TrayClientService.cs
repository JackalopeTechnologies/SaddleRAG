// TrayClientService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Drives SaddleRAG registration into AI agents from the tray menu, over the shared
///     <see cref="ClientWriterCatalog" />. Installing a single named agent forces it
///     (written even if not detected); "All" registers only agents detected on this machine,
///     mirroring the CLI's bulk semantics. Uninstall and status operate on every requested
///     agent and never throw — absent agents are reported as no-ops.
/// </summary>
public sealed class TrayClientService
{
    public async Task<string> InstallAsync(string? clientKey, CancellationToken ct)
    {
        string res;
        if (IsAll(clientKey))
        {
            ClientRegistrar registrar = new(AllWriters());
            RegistrarResult result = await registrar.RegisterDetectedAsync(SaddleRagEndpoint.Default, ct);
            res = ClientResultFormatter.SummarizeRegister(result);
        }
        else
        {
            IClientWriter? writer = ClientWriterCatalog.FindByKey(clientKey ?? string.Empty);
            if (writer is null)
            {
                res = $"{UnknownClientPrefix}{clientKey}";
            }
            else
            {
                ClientRegistrar registrar = new([writer]);
                RegistrarResult result = await registrar.RegisterAsync(SaddleRagEndpoint.Default, ct);
                res = ClientResultFormatter.SummarizeRegister(result);
            }
        }

        return res;
    }

    public async Task<string> UninstallAsync(string? clientKey, CancellationToken ct)
    {
        string res;
        if (IsAll(clientKey))
        {
            ClientRegistrar registrar = new(AllWriters());
            RegistrarResult result = await registrar.UnregisterAsync(ct);
            res = ClientResultFormatter.SummarizeUnregister(result);
        }
        else
        {
            IClientWriter? writer = ClientWriterCatalog.FindByKey(clientKey ?? string.Empty);
            if (writer is null)
            {
                res = $"{UnknownClientPrefix}{clientKey}";
            }
            else
            {
                ClientRegistrar registrar = new([writer]);
                RegistrarResult result = await registrar.UnregisterAsync(ct);
                res = ClientResultFormatter.SummarizeUnregister(result);
            }
        }

        return res;
    }

    public async Task<string> StatusAsync(CancellationToken ct)
    {
        List<ClientResultFormatter.StatusLineInput> inputs = [];
        foreach (ClientWriterCatalog.ClientDescriptor descriptor in ClientWriterCatalog.All)
        {
            IClientWriter writer = descriptor.Factory();
            StatusResult status = await writer.GetStatusAsync(ct);
            inputs.Add(new ClientResultFormatter.StatusLineInput(descriptor.DisplayName, status, writer.IsDetected()));
        }

        return ClientResultFormatter.SummarizeStatus(inputs);
    }

    private static bool IsAll(string? clientKey) =>
        string.IsNullOrEmpty(clientKey) || string.Equals(clientKey, AllKey, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<IClientWriter> AllWriters() =>
        ClientWriterCatalog.All.Select(d => d.Factory()).ToList();

    private const string AllKey = "all";
    private const string UnknownClientPrefix = "Unknown client: ";
}
