// IClientWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
