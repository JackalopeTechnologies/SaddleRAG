// RegisterResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.ClientIntegration.Models;

public sealed record RegisterResult(
    string ClientName,
    bool Success,
    string ConfigPath,
    string Message,
    string? SkillPath = null)
{
    /// <summary>
    ///     True when the agent was not detected on the machine and registration
    ///     was deliberately skipped. Distinguishes "skipped" from "actually wrote
    ///     it" for reporting; a skipped result still reports <see cref="Success"/>
    ///     true so a missing agent never fails the batch.
    /// </summary>
    public bool Skipped { get; init; }

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

    /// <summary>
    ///     Creates a skipped result (agent not detected). Counts as a success
    ///     for aggregation so a missing agent never fails the batch. Named
    ///     SkippedFor because the record already exposes a Skipped property.
    /// </summary>
    public static RegisterResult SkippedFor(string clientName, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientName);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        var result = new RegisterResult(clientName, Success: true, string.Empty, reason) { Skipped = true };
        return result;
    }
}
