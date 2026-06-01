// IClientWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.ClientIntegration;

public interface IClientWriter
{
    string ClientName { get; }

    /// <summary>
    ///     True when this agent appears installed on the machine (its config
    ///     directory or a marker exists). Used to register only detected agents.
    ///     Pure existence check — no I/O beyond Directory/File.Exists, never throws.
    /// </summary>
    bool IsDetected();

    Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct);

    Task<UnregisterResult> UnregisterAsync(CancellationToken ct);

    Task<StatusResult> GetStatusAsync(CancellationToken ct);
}
