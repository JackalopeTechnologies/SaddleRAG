// UnregisterResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.ClientIntegration.Models;

public sealed record UnregisterResult(
    string ClientName,
    bool Success,
    string ConfigPath,
    string Message,
    bool WasNoOp)
{
    public static UnregisterResult Removed(string clientName, string configPath, string message)
        => new(clientName, true, configPath, message, WasNoOp: false);

    public static UnregisterResult NoOp(string clientName, string configPath, string reason)
        => new(clientName, true, configPath, reason, WasNoOp: true);

    public static UnregisterResult Failed(string clientName, string configPath, string message)
        => new(clientName, false, configPath, message, WasNoOp: false);
}
