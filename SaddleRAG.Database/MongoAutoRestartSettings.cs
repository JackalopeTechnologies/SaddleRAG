// MongoAutoRestartSettings.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Database;

/// <summary>
///     Controls whether SaddleRAG restarts a stopped local MongoDB Windows service
///     when a request needs the database. Bound from <c>MongoDB:AutoRestart</c>.
/// </summary>
public sealed class MongoAutoRestartSettings
{
    /// <summary>
    ///     When true, a stopped local MongoDB service is started on demand.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Name of the Windows service that runs MongoDB.
    /// </summary>
    public string ServiceName { get; set; } = DefaultServiceName;

    internal const string DefaultServiceName = "MongoDB";
}
