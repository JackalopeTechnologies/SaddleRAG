// ClientRegistrar.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.ClientIntegration;

public sealed class ClientRegistrar
{
    private const string UnhandledExceptionPrefix = "unhandled exception: ";

    private readonly IReadOnlyList<IClientWriter> mWriters;

    public ClientRegistrar(IEnumerable<IClientWriter> writers)
    {
        mWriters = writers.ToList();
    }

    public async Task<RegistrarResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        List<RegisterResult> results = [];
        foreach (IClientWriter writer in mWriters)
        {
            RegisterResult result = await SafeRegisterAsync(writer, endpoint, ct);
            results.Add(result);
        }
        return RegistrarResult.ForRegister(results);
    }

    public async Task<RegistrarResult> UnregisterAsync(CancellationToken ct)
    {
        List<UnregisterResult> results = [];
        foreach (IClientWriter writer in mWriters)
        {
            UnregisterResult result = await SafeUnregisterAsync(writer, ct);
            results.Add(result);
        }
        return RegistrarResult.ForUnregister(results);
    }

    public async Task<IReadOnlyList<StatusResult>> GetStatusAsync(CancellationToken ct)
    {
        List<StatusResult> results = [];
        foreach (IClientWriter writer in mWriters)
        {
            StatusResult result = await writer.GetStatusAsync(ct);
            results.Add(result);
        }
        return results;
    }

    private static async Task<RegisterResult> SafeRegisterAsync(IClientWriter writer, SaddleRagEndpoint endpoint, CancellationToken ct)
    {
        RegisterResult res;
        try
        {
            res = await writer.RegisterAsync(endpoint, ct);
        }
        catch (Exception ex)
        {
            res = RegisterResult.Failed(writer.ClientName, string.Empty, $"{UnhandledExceptionPrefix}{ex.Message}");
        }
        return res;
    }

    private static async Task<UnregisterResult> SafeUnregisterAsync(IClientWriter writer, CancellationToken ct)
    {
        UnregisterResult res;
        try
        {
            res = await writer.UnregisterAsync(ct);
        }
        catch (Exception ex)
        {
            res = UnregisterResult.Failed(writer.ClientName, string.Empty, $"{UnhandledExceptionPrefix}{ex.Message}");
        }
        return res;
    }
}
