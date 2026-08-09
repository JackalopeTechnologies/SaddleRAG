// DoclingRegistration.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     A Docling Serve start command the user registered. SaddleRAG never installs,
///     licenses, configures, or upgrades Docling — it only records what to run.
/// </summary>
public sealed record DoclingRegistration(string Command,
                                         string Arguments,
                                         string WorkingDirectory,
                                         IReadOnlyDictionary<string, string>? Environment)
{
    private static readonly Dictionary<string, string> smNoEnvironment = [];

    /// <summary>
    ///     Environment overrides applied to the child process. Never null: a registry
    ///     file that omits the member reads back as an empty map.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = Environment ?? smNoEnvironment;
}
