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
    ///     The unreachable server address or addresses, as <c>host:port</c>.
    /// </summary>
    public string Endpoint { get; }

    private static string BuildMessage(string endpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        string message =
            $"SaddleRAG's database at {endpoint} is unreachable (MongoDB is not accepting connections). {Remedy}";
        return message;
    }

    private const string Remedy = "Start the MongoDB service, then retry.";
}
