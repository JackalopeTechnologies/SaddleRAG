// RegisterResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.ClientIntegration.Models;

public sealed record RegisterResult(
    string ClientName,
    bool Success,
    string ConfigPath,
    string Message,
    string? SkillPath = null)
{
    public static RegisterResult Ok(string clientName, string configPath, string message, string? skillPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientName);
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        ArgumentException.ThrowIfNullOrEmpty(message);
        var result = new RegisterResult(clientName, Success: true, configPath, message, skillPath);
        return result;
    }

    public static RegisterResult Failed(string clientName, string configPath, string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientName);
        ArgumentNullException.ThrowIfNull(configPath);
        ArgumentException.ThrowIfNullOrEmpty(message);
        var result = new RegisterResult(clientName, Success: false, configPath, message);
        return result;
    }
}
