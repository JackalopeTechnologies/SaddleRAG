// VsCodeMcpWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.ClientIntegration.Writers;

public sealed class VsCodeMcpWriter : IClientWriter
{
    private const string Name = "vscode-mcp";
    private const string ServersKey = "servers";
    private const string SaddleRagKey = "saddlerag";
    private const string TypeKey = "type";
    private const string UrlKey = "url";
    private const string HttpType = "http";
    private const string TmpSuffix = ".tmp";
    private const string AppDataCodeFolder = "Code";
    private const string AppDataUserFolder = "User";
    private const string AppDataMcpFile = "mcp.json";
    private const string SettingsFileName = "settings.json";
    private const string PluginRootFolderName = "saddlerag-plugin";
    private const string PluginManifestFileName = "plugin.json";
    private const string PluginMcpFileName = ".mcp.json";
    private const string PluginLocationsKey = "chat.pluginLocations";
    private const string PluginsEnabledKey = "chat.plugins.enabled";
    private const string McpAutoStartKey = "chat.mcp.autoStart";
    private const string LegacyMcpAutostartKey = "chat.mcp.autostart";
    private const string UseAgentSkillsKey = "chat.useAgentSkills";
    private const string AgentSkillsLocationsKey = "chat.agentSkillsLocations";
    private const string PluginMcpServersKey = "mcpServers";
    private const string PluginNameKey = "name";
    private const string PluginDescriptionKey = "description";
    private const string PluginVersionKey = "version";
    private const string PluginAuthorKey = "author";
    private const string PluginAuthorNameKey = "name";
    private const string CopilotSkillsLocation = "~/.copilot/skills";
    private const string ClaudeSkillsLocation = "~/.claude/skills";
    private const string McpAutostartAlways = "always";
    private const string PluginName = "jackalope-saddlerag";
    private const string PluginDescription = "SaddleRAG MCP integration for VS Code Copilot";
    private const string PluginAuthorName = "Jackalope Technologies, Inc.";
    private const int VersionFieldCount = 3;
    private const string DefaultPluginVersion = "1.0.0";
    private const string NotesSeparator = "; ";
    private const string MsgRegisterDidNotRun = "register did not run";
    private const string MsgRegistered = "registered VS Code plugin and settings updated";
    private const string MsgConfigFileMissing = "config file does not exist";
    private const string MsgSaddleRagRemoved = "saddlerag entry removed";
    private const string MsgSaddleRagNotPresent = "saddlerag entry was not present";
    private const string MsgSettingsMalformedPrefix = "settings file malformed: ";
    private const string MsgSettingsMissing = "VS Code user settings are not configured for SaddleRAG plugin discovery";
    private const string MsgAutostartNotAlways = "chat.mcp.autostart/chat.mcp.autoStart is not 'always'; existing MCP servers may require manual start";
    private const string MsgAgentSkillsDisabled = "chat.useAgentSkills is false; Copilot skills will not load";
    private const string MsgPluginsDisabled = "chat.plugins.enabled is false; VS Code agent plugins will not load";
    private const string MsgCopilotSkillsLocationMissing = "chat.agentSkillsLocations is missing ~/.copilot/skills";
    private const string MsgClaudeSkillsLocationMissing = "chat.agentSkillsLocations is missing ~/.claude/skills";
    private const string MsgPluginLocationMissing = "chat.pluginLocations is missing the SaddleRAG plugin path";
    private const string MsgPluginManifestMissing = "SaddleRAG plugin manifest is missing";
    private const string MsgPluginMcpMissing = "SaddleRAG plugin MCP config is missing";
    private const string MsgLegacyMcpEntryPresent = "legacy saddlerag entry still exists in user mcp.json";

    private static readonly JsonSerializerOptions smWriteOptions = new()
                                                                       {
                                                                           WriteIndented = true
                                                                       };

    private static readonly UTF8Encoding smUtf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string mConfigPath;
    private readonly string mSettingsPath;
    private readonly string mPluginRoot;
    private readonly string mPluginManifestPath;
    private readonly string mPluginMcpPath;

    public VsCodeMcpWriter(string configPath, string? settingsPath = null)
    {
        mConfigPath = configPath;
        string settingsDir = Path.GetDirectoryName(configPath) ?? string.Empty;
        string resolvedSettingsPath = Path.Combine(settingsDir, SettingsFileName);
        if (!string.IsNullOrWhiteSpace(settingsPath))
            resolvedSettingsPath = settingsPath;
        mSettingsPath = resolvedSettingsPath;
        string pluginBaseDir = Path.GetDirectoryName(mSettingsPath) ?? settingsDir;
        mPluginRoot = Path.Combine(pluginBaseDir, PluginRootFolderName);
        mPluginManifestPath = Path.Combine(mPluginRoot, PluginManifestFileName);
        mPluginMcpPath = Path.Combine(mPluginRoot, PluginMcpFileName);
    }

    public string ClientName => Name;

    public bool IsDetected()
    {
        string? userDir = Path.GetDirectoryName(mConfigPath);
        string? codeDir = userDir is null ? null : Path.GetDirectoryName(userDir);
        return codeDir is not null && Directory.Exists(codeDir);
    }

    public static VsCodeMcpWriter ForCurrentUser()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userDir = Path.Combine(appData, AppDataCodeFolder, AppDataUserFolder);
        string config = Path.Combine(userDir, AppDataMcpFile);
        string settings = Path.Combine(userDir, SettingsFileName);
        return new VsCodeMcpWriter(config, settings);
    }

    public async Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        RegisterResult res = RegisterResult.Failed(Name, mPluginManifestPath, MsgRegisterDidNotRun);
        try
        {
            JsonObject root = await LoadRootAsync(mConfigPath, ct);
            JsonObject settingsRoot = await LoadRootAsync(mSettingsPath, ct);
            RemoveSaddleRagEntry(root);
            ApplyCopilotSettings(settingsRoot);
            JsonObject pluginManifest = CreatePluginManifestRoot();
            JsonObject pluginMcpRoot = CreatePluginMcpRoot(endpoint);
            await SaveRootAsync(mConfigPath, root, ct);
            await SaveRootAsync(mSettingsPath, settingsRoot, ct);
            await SaveRootAsync(mPluginManifestPath, pluginManifest, ct);
            await SaveRootAsync(mPluginMcpPath, pluginMcpRoot, ct);
            res = RegisterResult.Ok(Name, mPluginManifestPath, MsgRegistered, mPluginRoot);
        }
        catch (JsonException ex)
        {
            res = RegisterResult.Failed(Name, mPluginManifestPath, $"failed to parse {mConfigPath}: {ex.Message}");
        }
        catch (IOException ex)
        {
            res = RegisterResult.Failed(Name, mPluginManifestPath, $"I/O error on {mPluginManifestPath}: {ex.Message}");
        }
        return res;
    }

    public async Task<UnregisterResult> UnregisterAsync(CancellationToken ct)
    {
        UnregisterResult res = UnregisterResult.NoOp(Name, mPluginManifestPath, MsgConfigFileMissing);
        bool removed = false;
        try
        {
            if (File.Exists(mConfigPath))
            {
                JsonObject root = await LoadRootAsync(mConfigPath, ct);
                bool mcpRemoved = RemoveSaddleRagEntry(root);
                if (mcpRemoved)
                {
                    await SaveRootAsync(mConfigPath, root, ct);
                    removed = true;
                }
            }

            if (File.Exists(mSettingsPath))
            {
                JsonObject settingsRoot = await LoadRootAsync(mSettingsPath, ct);
                bool pluginLocationRemoved = RemovePluginLocation(settingsRoot);
                if (pluginLocationRemoved)
                {
                    await SaveRootAsync(mSettingsPath, settingsRoot, ct);
                    removed = true;
                }
            }

            if (Directory.Exists(mPluginRoot))
            {
                Directory.Delete(mPluginRoot, recursive: true);
                removed = true;
            }

            res = removed
                      ? UnregisterResult.Removed(Name, mPluginManifestPath, MsgSaddleRagRemoved)
                      : UnregisterResult.NoOp(Name, mPluginManifestPath, MsgSaddleRagNotPresent);
        }
        catch (JsonException ex)
        {
            res = UnregisterResult.Failed(Name, mPluginManifestPath, $"failed to parse {mConfigPath}: {ex.Message}");
        }
        catch (IOException ex)
        {
            res = UnregisterResult.Failed(Name, mPluginManifestPath, $"I/O error on {mPluginManifestPath}: {ex.Message}");
        }
        return res;
    }

    public async Task<StatusResult> GetStatusAsync(CancellationToken ct)
    {
        bool pluginManifestExists = File.Exists(mPluginManifestPath);
        bool pluginMcpExists = File.Exists(mPluginMcpPath);
        bool settingsFileExists = File.Exists(mSettingsPath);
        bool entryPresent = false;
        bool endpointMatches = false;
        bool? pluginEnabled = null;
        bool? agentPluginsEnabled = null;
        List<string> noteParts = [];

        if (pluginManifestExists)
        {
            try
            {
                JsonObject pluginRoot = await LoadRootAsync(mPluginManifestPath, ct);
                string pluginName = GetStringValue(pluginRoot[PluginNameKey], string.Empty);
                if (!string.Equals(pluginName, PluginName, StringComparison.Ordinal))
                    noteParts.Add(MsgPluginManifestMissing);
            }
            catch (JsonException ex)
            {
                noteParts.Add($"config file malformed: {ex.Message}");
            }
        }
        else
            noteParts.Add(MsgPluginManifestMissing);

        if (pluginMcpExists)
        {
            try
            {
                JsonObject root = await LoadRootAsync(mPluginMcpPath, ct);
                JsonObject? servers = root[PluginMcpServersKey] as JsonObject;
                JsonObject? entry = servers?[SaddleRagKey] as JsonObject;
                if (entry is not null)
                {
                    string? url = entry[UrlKey]?.GetValue<string>();
                    endpointMatches = string.Equals(url, SaddleRagEndpoint.Default.Url, StringComparison.Ordinal);
                }
            }
            catch (JsonException ex)
            {
                noteParts.Add($"config file malformed: {ex.Message}");
            }
        }
        else
            noteParts.Add(MsgPluginMcpMissing);

        if (File.Exists(mConfigPath))
        {
            try
            {
                JsonObject root = await LoadRootAsync(mConfigPath, ct);
                bool hasLegacyEntry = HasSaddleRagEntry(root);
                if (hasLegacyEntry)
                    noteParts.Add(MsgLegacyMcpEntryPresent);
            }
            catch (JsonException ex)
            {
                noteParts.Add($"config file malformed: {ex.Message}");
            }
        }

        if (settingsFileExists)
        {
            try
            {
                JsonObject settingsRoot = await LoadRootAsync(mSettingsPath, ct);
                entryPresent = AppendSettingsNotes(settingsRoot, noteParts, out bool pluginsEnabled);
                pluginEnabled = entryPresent;
                agentPluginsEnabled = pluginsEnabled;
            }
            catch (JsonException ex)
            {
                noteParts.Add(MsgSettingsMalformedPrefix + ex.Message);
            }
        }
        else
            noteParts.Add(MsgSettingsMissing);

        string notes = string.Join(NotesSeparator, noteParts);
        return new StatusResult(
            Name,
            mPluginManifestPath,
            pluginManifestExists,
            entryPresent,
            endpointMatches,
            SkillFilePresent: null,
            notes,
            PluginPath: mPluginRoot,
            PluginEnabled: pluginEnabled,
            AgentPluginsEnabled: agentPluginsEnabled);
    }

    private void ApplyCopilotSettings(JsonObject settingsRoot)
    {
        settingsRoot[McpAutoStartKey] = McpAutostartAlways;
        settingsRoot[LegacyMcpAutostartKey] = McpAutostartAlways;
        settingsRoot[UseAgentSkillsKey] = true;
        settingsRoot[PluginsEnabledKey] = true;
        JsonObject skillLocations = (settingsRoot[AgentSkillsLocationsKey] as JsonObject) ?? new JsonObject();
        skillLocations[CopilotSkillsLocation] = true;
        skillLocations[ClaudeSkillsLocation] = true;
        settingsRoot[AgentSkillsLocationsKey] = skillLocations;
        JsonObject pluginLocations = (settingsRoot[PluginLocationsKey] as JsonObject) ?? new JsonObject();
        pluginLocations[mPluginRoot] = true;
        settingsRoot[PluginLocationsKey] = pluginLocations;
    }

    private bool AppendSettingsNotes(JsonObject settingsRoot, List<string> noteParts, out bool pluginsEnabled)
    {
        bool res;
        string autostartMode = GetStringValue(settingsRoot[McpAutoStartKey], string.Empty);
        if (string.IsNullOrEmpty(autostartMode))
            autostartMode = GetStringValue(settingsRoot[LegacyMcpAutostartKey], string.Empty);
        if (!string.Equals(autostartMode, McpAutostartAlways, StringComparison.Ordinal))
            noteParts.Add(MsgAutostartNotAlways);

        bool useAgentSkills = GetBooleanValue(settingsRoot[UseAgentSkillsKey], defaultValue: true);
        if (!useAgentSkills)
            noteParts.Add(MsgAgentSkillsDisabled);

        pluginsEnabled = GetBooleanValue(settingsRoot[PluginsEnabledKey], defaultValue: true);
        if (!pluginsEnabled)
            noteParts.Add(MsgPluginsDisabled);

        JsonObject skillLocations = (settingsRoot[AgentSkillsLocationsKey] as JsonObject) ?? new JsonObject();
        bool hasCopilotSkills = GetBooleanValue(skillLocations[CopilotSkillsLocation], defaultValue: false);
        bool hasClaudeSkills = GetBooleanValue(skillLocations[ClaudeSkillsLocation], defaultValue: false);
        if (!hasCopilotSkills)
            noteParts.Add(MsgCopilotSkillsLocationMissing);
        if (!hasClaudeSkills)
            noteParts.Add(MsgClaudeSkillsLocationMissing);

        JsonObject pluginLocations = (settingsRoot[PluginLocationsKey] as JsonObject) ?? new JsonObject();
        res = GetBooleanValue(pluginLocations[mPluginRoot], defaultValue: false);
        if (!res)
            noteParts.Add(MsgPluginLocationMissing);
        return res;
    }

    private static JsonObject CreatePluginManifestRoot()
    {
        string version = GetPluginVersion();
        JsonObject res = new()
                         {
                             [PluginNameKey] = PluginName,
                             [PluginDescriptionKey] = PluginDescription,
                             [PluginVersionKey] = version,
                             [PluginAuthorKey] = new JsonObject
                                                 {
                                                     [PluginAuthorNameKey] = PluginAuthorName
                                                 },
                             [PluginMcpServersKey] = PluginMcpFileName
                         };
        return res;
    }

    private static JsonObject CreatePluginMcpRoot(SaddleRagEndpoint endpoint)
    {
        JsonObject res = new()
                         {
                             [PluginMcpServersKey] = new JsonObject
                                                    {
                                                        [SaddleRagKey] = new JsonObject
                                                                        {
                                                                            [TypeKey] = HttpType,
                                                                            [UrlKey] = endpoint.Url
                                                                        }
                                                    }
                         };
        return res;
    }

    private static string GetPluginVersion()
    {
        string res = DefaultPluginVersion;
        Version? version = typeof(VsCodeMcpWriter).Assembly.GetName().Version;
        if (version is not null)
            res = version.ToString(VersionFieldCount);
        return res;
    }

    private static bool HasSaddleRagEntry(JsonObject root)
    {
        bool res = false;
        if (root[ServersKey] is JsonObject servers)
            res = FindSaddleRagServerKey(servers) is not null;
        return res;
    }

    private static bool RemoveSaddleRagEntry(JsonObject root)
    {
        bool res = false;
        if (root[ServersKey] is JsonObject servers)
        {
            string? saddleRagKey = FindSaddleRagServerKey(servers);
            if (saddleRagKey is not null)
            {
                servers.Remove(saddleRagKey);
                res = true;
            }
        }
        return res;
    }

    private static string? FindSaddleRagServerKey(JsonObject servers)
    {
        string? res = null;
        foreach (KeyValuePair<string, JsonNode?> entry in servers)
        {
            if (string.Equals(entry.Key, SaddleRagKey, StringComparison.OrdinalIgnoreCase))
            {
                res = entry.Key;
                break;
            }
        }
        return res;
    }

    private bool RemovePluginLocation(JsonObject settingsRoot)
    {
        bool res = false;
        if (settingsRoot[PluginLocationsKey] is JsonObject pluginLocations && pluginLocations.ContainsKey(mPluginRoot))
        {
            pluginLocations.Remove(mPluginRoot);
            if (pluginLocations.Count == 0)
                settingsRoot.Remove(PluginLocationsKey);
            res = true;
        }
        return res;
    }

    private static bool GetBooleanValue(JsonNode? node, bool defaultValue)
    {
        bool res = defaultValue;
        if (node is JsonValue value && value.TryGetValue<bool>(out bool parsed))
            res = parsed;
        return res;
    }

    private static string GetStringValue(JsonNode? node, string defaultValue)
    {
        string res = defaultValue;
        if (node is JsonValue value && value.TryGetValue<string>(out string? parsed) && parsed is not null)
            res = parsed;
        return res;
    }

    private static async Task<JsonObject> LoadRootAsync(string path, CancellationToken ct)
    {
        JsonObject root = new();
        if (File.Exists(path))
        {
            string text = await File.ReadAllTextAsync(path, ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                JsonNode? parsed = JsonNode.Parse(text);
                if (parsed is JsonObject obj)
                    root = obj;
            }
        }
        return root;
    }

    private static async Task SaveRootAsync(string path, JsonObject root, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        string tmp = path + TmpSuffix;
        string serialized = root.ToJsonString(smWriteOptions);
        await File.WriteAllTextAsync(tmp, serialized, smUtf8NoBom, ct);
        File.Move(tmp, path, overwrite: true);
    }
}
