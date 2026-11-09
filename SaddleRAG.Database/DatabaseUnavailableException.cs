// DatabaseUnavailableException.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Database;

/// <summary>
///     Raised when SaddleRAG's MongoDB database cannot be reached. The message is
///     written for the calling client: it names the endpoint and the remedy, so it
///     can be shown as-is instead of a generic tool failure.
/// </summary>
public sealed class DatabaseUnavailableException : Exception
{
    public DatabaseUnavailableException(string endpoint, Exception? innerException)
        : base(BuildMessage(endpoint), innerException)
    {
        Endpoint = endpoint;
    }

    /// <summary>
    ///     Raised after SaddleRAG tried to restart the local MongoDB service. The
    ///     <paramref name="restartReport" /> says what it tried and how that ended, so
    ///     the caller sees it in the same one-line message.
    /// </summary>
    public DatabaseUnavailableException(string endpoint, string restartReport, Exception? innerException)
        : base(BuildMessage(endpoint, restartReport), innerException)
    {
        Endpoint = endpoint;
        RestartReport = restartReport;
    }

    /// <summary>
    ///     The unreachable server address or addresses, as <c>host:port</c>.
    /// </summary>
    public string Endpoint { get; }

    /// <summary>
    ///     What SaddleRAG tried to restart and how that ended; null when no restart
    ///     was attempted.
    /// </summary>
    public string? RestartReport { get; }

    private static string BuildMessage(string endpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        string message =
            $"SaddleRAG's database at {endpoint} is unreachable (MongoDB is not accepting connections). {Remedy}";
        return message;
    }

    private static string BuildMessage(string endpoint, string restartReport)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(restartReport);
        string message =
            $"SaddleRAG's database at {endpoint} is unreachable (MongoDB is not accepting connections). {restartReport} {Remedy}";
        return message;
    }

    private const string Remedy = "Start the MongoDB service, then retry.";
}
