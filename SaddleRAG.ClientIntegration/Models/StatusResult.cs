// StatusResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

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
