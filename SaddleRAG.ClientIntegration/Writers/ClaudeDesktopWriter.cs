// ClaudeDesktopWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.ClientIntegration.Writers;

public sealed class ClaudeDesktopWriter : IClientWriter
{
    private const string Name = "claude-desktop";
    private const string McpServersKey = "mcpServers";
    private const string SaddleRagKey = "saddlerag";
    private const string CommandKey = "command";
    private const string ArgsKey = "args";
    private const string NpxCommand = "npx";
    private const string McpRemoteFlag = "-y";
    private const string McpRemotePackage = "mcp-remote@latest";
    private const string McpRemoteAllowHttp = "--allow-http";
    private const string TmpSuffix = ".tmp";
    private const string DesktopAppDataFolder = "AppData";
    private const string DesktopAppDataRoamingFolder = "Roaming";
    private const string DesktopClaudeFolder = "Claude";
    private const string DesktopConfigFileName = "claude_desktop_config.json";
    private const string MsgRegisterDidNotRun = "register did not run";
    private const string MsgRegistered = "registered";
    private const string MsgConfigFileMissing = "config file does not exist";
    private const string MsgSaddleRagRemoved = "saddlerag entry removed";
    private const string MsgSaddleRagNotPresent = "saddlerag entry was not present";
    private const string MsgNoSkillConcept = "Claude Desktop has no skill concept";

    private static readonly JsonSerializerOptions smWriteOptions = new()
                                                                       {
                                                                           WriteIndented = true
                                                                       };

    private static readonly UTF8Encoding smUtf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string mConfigPath;

    public ClaudeDesktopWriter(string configPath)
    {
        mConfigPath = configPath;
    }

    public string ClientName => Name;

    public static ClaudeDesktopWriter ForCurrentUser()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string config = Path.Combine(profile, DesktopAppDataFolder, DesktopAppDataRoamingFolder, DesktopClaudeFolder, DesktopConfigFileName);
        return new ClaudeDesktopWriter(config);
    }

    public async Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        RegisterResult res = RegisterResult.Failed(Name, mConfigPath, MsgRegisterDidNotRun);
        try
        {
            JsonObject root = await LoadRootAsync(ct);
            JsonObject servers = (root[McpServersKey] as JsonObject) ?? new JsonObject();
            servers[SaddleRagKey] = new JsonObject
                                        {
                                            [CommandKey] = NpxCommand,
                                            [ArgsKey] = new JsonArray(McpRemoteFlag, McpRemotePackage, endpoint.Url, McpRemoteAllowHttp)
                                        };
            root[McpServersKey] = servers;
            await SaveRootAsync(root, ct);
            res = RegisterResult.Ok(Name, mConfigPath, MsgRegistered);
        }
        catch (JsonException ex)
        {
            res = RegisterResult.Failed(Name, mConfigPath, $"failed to parse {mConfigPath}: {ex.Message}");
        }
        catch (IOException ex)
        {
            res = RegisterResult.Failed(Name, mConfigPath, $"I/O error on {mConfigPath}: {ex.Message}");
        }
        return res;
    }

    public async Task<UnregisterResult> UnregisterAsync(CancellationToken ct)
    {
        UnregisterResult res = UnregisterResult.NoOp(Name, mConfigPath, MsgConfigFileMissing);
        if (File.Exists(mConfigPath))
        {
            try
            {
                JsonObject root = await LoadRootAsync(ct);
                bool removed = false;
                if (root[McpServersKey] is JsonObject servers && servers.ContainsKey(SaddleRagKey))
                {
                    servers.Remove(SaddleRagKey);
                    removed = true;
                }
                if (removed)
                {
                    await SaveRootAsync(root, ct);
                    res = UnregisterResult.Removed(Name, mConfigPath, MsgSaddleRagRemoved);
                }
                else
                    res = UnregisterResult.NoOp(Name, mConfigPath, MsgSaddleRagNotPresent);
            }
            catch (JsonException ex)
            {
                res = UnregisterResult.Failed(Name, mConfigPath, $"failed to parse {mConfigPath}: {ex.Message}");
            }
            catch (IOException ex)
            {
                res = UnregisterResult.Failed(Name, mConfigPath, $"I/O error on {mConfigPath}: {ex.Message}");
            }
        }
        return res;
    }

    public async Task<StatusResult> GetStatusAsync(CancellationToken ct)
    {
        bool fileExists = File.Exists(mConfigPath);
        bool entryPresent = false;
        bool endpointMatches = false;
        string notes = MsgNoSkillConcept;
        if (fileExists)
        {
            try
            {
                JsonObject root = await LoadRootAsync(ct);
                JsonObject? servers = root[McpServersKey] as JsonObject;
                JsonObject? entry = servers?[SaddleRagKey] as JsonObject;
                entryPresent = entry is not null;
                if (entry is not null)
                    endpointMatches = ArgsContainUrl(entry[ArgsKey] as JsonArray, SaddleRagEndpoint.Default.Url);
            }
            catch (JsonException ex)
            {
                notes = $"config file malformed: {ex.Message}";
            }
        }
        return new StatusResult(
            Name,
            mConfigPath,
            fileExists,
            entryPresent,
            endpointMatches,
            SkillFilePresent: null,
            notes);
    }

    private async Task<JsonObject> LoadRootAsync(CancellationToken ct)
    {
        JsonObject root = new();
        if (File.Exists(mConfigPath))
        {
            string text = await File.ReadAllTextAsync(mConfigPath, ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                JsonNode? parsed = JsonNode.Parse(text);
                if (parsed is JsonObject obj)
                    root = obj;
            }
        }
        return root;
    }

    private async Task SaveRootAsync(JsonObject root, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(mConfigPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        string tmp = mConfigPath + TmpSuffix;
        string serialized = root.ToJsonString(smWriteOptions);
        await File.WriteAllTextAsync(tmp, serialized, smUtf8NoBom, ct);
        File.Move(tmp, mConfigPath, overwrite: true);
    }

    private static bool ArgsContainUrl(JsonArray? args, string url)
    {
        bool found = false;
        if (args is not null)
        {
            foreach (JsonNode? node in args)
            {
                string? value = node?.GetValue<string>();
                if (string.Equals(value, url, StringComparison.Ordinal))
                    found = true;
            }
        }
        return found;
    }
}
