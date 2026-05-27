// UnregisterResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.ClientIntegration.Models;

public sealed record UnregisterResult(
    string ClientName,
    bool Success,
    string ConfigPath,
    string Message,
    bool WasNoOp)
{
    public static UnregisterResult Removed(string clientName, string configPath, string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientName);
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        ArgumentException.ThrowIfNullOrEmpty(message);
        var result = new UnregisterResult(clientName, Success: true, configPath, message, WasNoOp: false);
        return result;
    }

    public static UnregisterResult NoOp(string clientName, string configPath, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientName);
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        var result = new UnregisterResult(clientName, Success: true, configPath, reason, WasNoOp: true);
        return result;
    }

    public static UnregisterResult Failed(string clientName, string configPath, string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientName);
        ArgumentNullException.ThrowIfNull(configPath);
        ArgumentException.ThrowIfNullOrEmpty(message);
        var result = new UnregisterResult(clientName, Success: false, configPath, message, WasNoOp: false);
        return result;
    }
}
