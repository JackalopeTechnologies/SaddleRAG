// CopilotCliWriter.cs
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

public sealed class CopilotCliWriter : IClientWriter
{
    private const string Name = "copilot-cli";
    private const string McpServersKey = "mcpServers";
    private const string SaddleRagKey = "saddlerag";
    private const string TypeKey = "type";
    private const string UrlKey = "url";
    private const string SseType = "sse";
    private const string CopilotHomeEnvVar = "COPILOT_HOME";
    private const string DefaultCopilotDir = ".copilot";
    private const string McpConfigFileName = "mcp-config.json";
    private const string SkillsSubDir = "skills";
    private const string SkillFolderName = "saddlerag-first";
    private const string SkillFileName = "SKILL.md";
    private const string SkillResourceName = "SaddleRAG.ClientIntegration.Resources.saddlerag-first.md";
    private const string TmpSuffix = ".tmp";
    private const string MsgRegisterDidNotRun = "register did not run";
    private const string MsgRegistered = "registered";
    private const string MsgConfigFileMissing = "config file does not exist";
    private const string MsgSaddleRagRemoved = "saddlerag entry removed";
    private const string MsgSaddleRagNotPresent = "saddlerag entry was not present";

    private static readonly JsonSerializerOptions smWriteOptions = new()
                                                                       {
                                                                           WriteIndented = true
                                                                       };

    private static readonly UTF8Encoding smUtf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string mConfigPath;
    private readonly string mSkillPath;

    public CopilotCliWriter(string configPath, string skillPath)
    {
        mConfigPath = configPath;
        mSkillPath = skillPath;
    }

    public string ClientName => Name;

    public string ConfigPath => mConfigPath;

    public string SkillPath => mSkillPath;

    public static CopilotCliWriter ForCurrentUser()
    {
        string home = Environment.GetEnvironmentVariable(CopilotHomeEnvVar)
                      ?? Path.Combine(
                          Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                          DefaultCopilotDir);
        string config = Path.Combine(home, McpConfigFileName);
        string skill = Path.Combine(home, SkillsSubDir, SkillFolderName, SkillFileName);
        return new CopilotCliWriter(config, skill);
    }

    public async Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct)
    {
        RegisterResult res = RegisterResult.Failed(Name, mConfigPath, MsgRegisterDidNotRun);
        try
        {
            JsonObject root = await LoadRootAsync(ct);
            ApplyMcpEntry(root, endpoint);
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
            catch (IOException ex)
            {
                res = UnregisterResult.Failed(Name, mConfigPath, $"I/O error on {mConfigPath}: {ex.Message}");
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
        string notes = string.Empty;
        if (fileExists)
        {
            try
            {
                JsonObject root = await LoadRootAsync(ct);
                JsonObject? servers = root[McpServersKey] as JsonObject;
                JsonObject? entry = servers?[SaddleRagKey] as JsonObject;
                entryPresent = entry is not null;
                if (entry is not null)
                {
                    string? url = entry[UrlKey]?.GetValue<string>();
                    endpointMatches = string.Equals(url, SaddleRagEndpoint.Default.Url, StringComparison.Ordinal);
                }
            }
            catch (JsonException ex)
            {
                notes = $"config file malformed: {ex.Message}";
            }
        }
        return new StatusResult(
            ClientName: Name,
            ConfigPath: mConfigPath,
            ConfigFileExists: fileExists,
            SaddleRagEntryPresent: entryPresent,
            EndpointMatchesCanonical: endpointMatches,
            SkillFilePresent: File.Exists(mSkillPath),
            Notes: notes);
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
        string serialized = root.ToJsonString(smWriteOptions);
        await File.WriteAllTextAsync(tmp, serialized, smUtf8NoBom, ct);
        File.Move(tmp, mConfigPath, overwrite: true);
    }

    private static void ApplyMcpEntry(JsonObject root, SaddleRagEndpoint endpoint)
    {
        JsonObject servers = (root[McpServersKey] as JsonObject) ?? [];
        servers[SaddleRagKey] = new JsonObject
                                    {
                                        [TypeKey] = SseType,
                                        [UrlKey] = endpoint.Url
                                    };
        root[McpServersKey] = servers;
    }

    private static bool RemoveSaddleRagEntry(JsonObject root)
    {
        bool removed = false;
        if (root[McpServersKey] is JsonObject servers && servers.ContainsKey(SaddleRagKey))
        {
            servers.Remove(SaddleRagKey);
            removed = true;
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
        Assembly asm = typeof(CopilotCliWriter).Assembly;
        await using Stream? stream = asm.GetManifestResourceStream(SkillResourceName)
                                     ?? throw new InvalidOperationException($"Embedded resource not found: {SkillResourceName}");
        using StreamReader reader = new(stream, Encoding.UTF8);
        string content = await reader.ReadToEndAsync(ct);
        await File.WriteAllTextAsync(mSkillPath, content, smUtf8NoBom, ct);
    }
}
