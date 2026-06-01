// StatusResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.ClientIntegration.Models;

public sealed record StatusResult(
    string ClientName,
    string ConfigPath,
    bool ConfigFileExists,
    bool SaddleRagEntryPresent,
    bool EndpointMatchesCanonical,
    bool? SkillFilePresent,
    string Notes,
    string? PluginPath = null,
    bool? PluginEnabled = null,
    bool? AgentPluginsEnabled = null);
