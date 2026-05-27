// IClientWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.ClientIntegration;

public interface IClientWriter
{
    string ClientName { get; }

    Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct);

    Task<UnregisterResult> UnregisterAsync(CancellationToken ct);

    Task<StatusResult> GetStatusAsync(CancellationToken ct);
}
