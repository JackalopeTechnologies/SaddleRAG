// ClaudeCodeWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.ClientIntegration.Writers;

public sealed class ClaudeCodeWriter : IClientWriter
{
    private const string Name = "claude-code";
    private const string SkillResourceName = "SaddleRAG.ClientIntegration.Resources.saddlerag-first.md";
    private const string KeyMcpServers = "mcpServers";
    private const string KeySaddleRag = "saddlerag";
    private const string KeyPermissions = "permissions";
    private const string KeyAllow = "allow";
    private const string KeyType = "type";
    private const string KeyUrl = "url";
    private const string TmpSuffix = ".tmp";

    private const string ConfigFileName = ".claude.json";
    private const string ClaudeUserDir = ".claude";
    private const string SkillsSubDir = "skills";
    private const string SkillFolderName = "saddlerag-first";
    private const string SkillFileName = "SKILL.md";

    private const string MsgRegisterDidNotRun = "register did not run";
    private const string MsgRegistered = "registered";
    private const string MsgConfigFileMissing = "config file does not exist";
    private const string MsgSaddleRagRemoved = "saddlerag entry removed";
    private const string MsgSaddleRagNotPresent = "saddlerag entry was not present";

    private static readonly JsonSerializerOptions psWriteOptions = new()
                                                                       {
                                                                           WriteIndented = true
                                                                       };

    private static readonly UTF8Encoding psUtf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string mConfigPath;
    private readonly string mSkillPath;

    public ClaudeCodeWriter(string configPath, string skillPath)
    {
        mConfigPath = configPath;
        mSkillPath = skillPath;
    }

    public string ClientName => Name;

    public static ClaudeCodeWriter ForCurrentUser()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string config = Path.Combine(profile, ConfigFileName);
        string skill = Path.Combine(profile, ClaudeUserDir, SkillsSubDir, SkillFolderName, SkillFileName);
        return new ClaudeCodeWriter(config, skill);
    }

    public async Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct)
    {
        RegisterResult res = RegisterResult.Failed(Name, mConfigPath, MsgRegisterDidNotRun);
        try
        {
            JsonObject root = await LoadRootAsync(ct);
            ApplyMcpEntry(root, endpoint);
            ApplyPermissionsAllow(root, endpoint.ReadOnlyToolPermissions);
            await SaveRootAsync(root, ct);
            await WriteSkillFileAsync(ct);
            res = RegisterResult.Ok(Name, mConfigPath, MsgRegistered, mSkillPath);
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
                bool removed = RemoveSaddleRagEntry(root);
                if (removed)
                {
                    await SaveRootAsync(root, ct);
                    res = UnregisterResult.Removed(Name, mConfigPath, MsgSaddleRagRemoved);
                }
                else
                {
                    res = UnregisterResult.NoOp(Name, mConfigPath, MsgSaddleRagNotPresent);
                }
            }
            catch (JsonException ex)
            {
                res = UnregisterResult.Failed(Name, mConfigPath, $"failed to parse {mConfigPath}: {ex.Message}");
            }
        }
        if (File.Exists(mSkillPath))
        {
            File.Delete(mSkillPath);
            string? skillDir = Path.GetDirectoryName(mSkillPath);
            if (!string.IsNullOrEmpty(skillDir) && Directory.Exists(skillDir) && !Directory.EnumerateFileSystemEntries(skillDir).Any())
            {
                Directory.Delete(skillDir);
            }
        }
        return res;
    }

    public async Task<StatusResult> GetStatusAsync(CancellationToken ct)
    {
        bool fileExists = File.Exists(mConfigPath);
        bool entryPresent = false;
        bool endpointMatches = false;
        if (fileExists)
        {
            try
            {
                JsonObject root = await LoadRootAsync(ct);
                JsonObject? servers = root[KeyMcpServers] as JsonObject;
                JsonObject? entry = servers?[KeySaddleRag] as JsonObject;
                entryPresent = entry is not null;
                if (entry is not null)
                {
                    string? url = entry[KeyUrl]?.GetValue<string>();
                    endpointMatches = string.Equals(url, SaddleRagEndpoint.Default.Url, StringComparison.Ordinal);
                }
            }
            catch (JsonException)
            {
            }
        }
        return new StatusResult(
            ClientName: Name,
            ConfigPath: mConfigPath,
            ConfigFileExists: fileExists,
            SaddleRagEntryPresent: entryPresent,
            EndpointMatchesCanonical: endpointMatches,
            SkillFilePresent: File.Exists(mSkillPath),
            Notes: string.Empty);
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
                {
                    root = obj;
                }
            }
        }
        return root;
    }

    private async Task SaveRootAsync(JsonObject root, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(mConfigPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string tmp = mConfigPath + TmpSuffix;
        string serialized = root.ToJsonString(psWriteOptions);
        await File.WriteAllTextAsync(tmp, serialized, psUtf8NoBom, ct);
        File.Move(tmp, mConfigPath, overwrite: true);
    }

    private static void ApplyMcpEntry(JsonObject root, SaddleRagEndpoint endpoint)
    {
        JsonObject servers = (root[KeyMcpServers] as JsonObject) ?? [];
        servers[KeySaddleRag] = new JsonObject
                                    {
                                        [KeyType] = "http",
                                        [KeyUrl] = endpoint.Url,
                                        ["timeout"] = endpoint.TimeoutSeconds
                                    };
        root[KeyMcpServers] = servers;
    }

    private static void ApplyPermissionsAllow(JsonObject root, IReadOnlyList<string> tools)
    {
        JsonObject permissions = (root[KeyPermissions] as JsonObject) ?? [];
        JsonArray allow = (permissions[KeyAllow] as JsonArray) ?? [];
        HashSet<string> existing = new(StringComparer.Ordinal);
        foreach (JsonNode? node in allow)
        {
            string? value = node?.GetValue<string>();
            if (value is not null)
            {
                existing.Add(value);
            }
        }
        foreach (string tool in tools.Where(t => !existing.Contains(t)))
        {
            allow.Add(tool);
        }
        permissions[KeyAllow] = allow;
        root[KeyPermissions] = permissions;
    }

    private static bool RemoveSaddleRagEntry(JsonObject root)
    {
        bool removed = false;
        if (root[KeyMcpServers] is JsonObject servers && servers.ContainsKey(KeySaddleRag))
        {
            servers.Remove(KeySaddleRag);
            removed = true;
        }
        if (root[KeyPermissions] is JsonObject permissions && permissions[KeyAllow] is JsonArray allow)
        {
            List<JsonNode?> toRemove = allow
                .Where(n => n?.GetValue<string>().Contains(KeySaddleRag, StringComparison.Ordinal) == true)
                .ToList();
            foreach (JsonNode? node in toRemove)
            {
                allow.Remove(node);
            }
        }
        return removed;
    }

    private async Task WriteSkillFileAsync(CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(mSkillPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        Assembly asm = typeof(ClaudeCodeWriter).Assembly;
        await using Stream? stream = asm.GetManifestResourceStream(SkillResourceName)
                                     ?? throw new InvalidOperationException($"Embedded resource not found: {SkillResourceName}");
        using StreamReader reader = new(stream, Encoding.UTF8);
        string content = await reader.ReadToEndAsync(ct);
        await File.WriteAllTextAsync(mSkillPath, content, psUtf8NoBom, ct);
    }
}
