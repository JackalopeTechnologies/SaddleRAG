// MonitorConfigMongo.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     MongoDB card on the Monitor /config page (issue #73).
///     <see cref="Host" /> is the connection string with the
///     user:password segment masked to <c>***:***</c>;
///     <see cref="CredentialsPresent" /> tells the page whether any
///     credentials were actually configured (vs. an anonymous local
///     connection). <see cref="AutoRestartEnabled" /> and
///     <see cref="AutoRestartServiceName" /> report whether SaddleRAG restarts a
///     stopped local MongoDB Windows service when a request needs it.
/// </summary>
public sealed record MonitorConfigMongo(
    string ActiveProfileName,
    string Host,
    string DatabaseName,
    bool CredentialsPresent,
    bool AutoRestartEnabled,
    string AutoRestartServiceName)
{
    /// <summary>One-line text for the config page: On with the service name, or Off.</summary>
    public string AutoRestartSummary =>
        AutoRestartEnabled
            ? $"{AutoRestartLabel}: On (service '{AutoRestartServiceName}')"
            : $"{AutoRestartLabel}: Off";

    private const string AutoRestartLabel = "Restart MongoDB automatically";
}
