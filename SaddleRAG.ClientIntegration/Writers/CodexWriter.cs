// CodexWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.ClientIntegration.Models;
using Tomlyn;
using Tomlyn.Model;

#endregion

namespace SaddleRAG.ClientIntegration.Writers;

public sealed class CodexWriter : IClientWriter
{
    private const string Name = "codex";
    private const string CodexDirName = ".codex";
    private const string ConfigFileName = "config.toml";
    private const string TmpSuffix = ".tmp";

    private const string FeaturesTable = "features";
    private const string RmcpFlag = "experimental_use_rmcp_client";
    private const string McpServersTable = "mcp_servers";
    private const string SaddleRagKey = "saddlerag";
    private const string UrlKey = "url";

    private const string MsgRegisterDidNotRun = "register did not run";
    private const string MsgRegistered = "registered";
    private const string MsgConfigFileMissing = "config file does not exist";
    private const string MsgSaddleRagRemoved = "saddlerag entry removed";
    private const string MsgSaddleRagNotPresent = "saddlerag entry was not present";

    private readonly string mConfigPath;

    public CodexWriter(string configPath)
    {
        mConfigPath = configPath;
    }

    public string ClientName => Name;

    public string ConfigPath => mConfigPath;

    public bool IsDetected()
    {
        string? codexDir = Path.GetDirectoryName(mConfigPath);
        return codexDir is not null && Directory.Exists(codexDir);
    }

    public static CodexWriter ForCurrentUser()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string config = Path.Combine(profile, CodexDirName, ConfigFileName);
        return new CodexWriter(config);
    }

    public async Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        RegisterResult res = RegisterResult.Failed(Name, mConfigPath, MsgRegisterDidNotRun);
        try
        {
            TomlTable root = await LoadRootAsync(ct);
            ApplyEntry(root, endpoint);
            await SaveRootAsync(root, ct);
            res = RegisterResult.Ok(Name, mConfigPath, MsgRegistered, string.Empty);
        }
        catch (TomlException ex)
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
                TomlTable root = await LoadRootAsync(ct);
                bool removed = RemoveEntry(root);
                if (removed)
                {
                    await SaveRootAsync(root, ct);
                    res = UnregisterResult.Removed(Name, mConfigPath, MsgSaddleRagRemoved);
                }
                else
                    res = UnregisterResult.NoOp(Name, mConfigPath, MsgSaddleRagNotPresent);
            }
            catch (TomlException ex)
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
        string notes = string.Empty;
        if (fileExists)
        {
            try
            {
                TomlTable root = await LoadRootAsync(ct);
                if (root.TryGetValue(McpServersTable, out object? serversObj) && serversObj is TomlTable servers
                    && servers.TryGetValue(SaddleRagKey, out object? entryObj) && entryObj is TomlTable entry)
                {
                    entryPresent = true;
                    string url = entry.TryGetValue(UrlKey, out object? urlObj) ? urlObj as string ?? string.Empty : string.Empty;
                    endpointMatches = string.Equals(url, SaddleRagEndpoint.Default.Url, StringComparison.Ordinal);
                }
            }
            catch (TomlException ex)
            {
                notes = $"config file malformed: {ex.Message}";
            }
        }
        return new StatusResult(Name, mConfigPath, fileExists, entryPresent, endpointMatches, null, notes);
    }

    private async Task<TomlTable> LoadRootAsync(CancellationToken ct)
    {
        TomlTable root = new();
        if (File.Exists(mConfigPath))
        {
            string text = await File.ReadAllTextAsync(mConfigPath, ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                TomlTable? parsed = TomlSerializer.Deserialize<TomlTable>(text);
                if (parsed is not null)
                    root = parsed;
            }
        }
        return root;
    }

    private async Task SaveRootAsync(TomlTable root, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(mConfigPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        string tmp = mConfigPath + TmpSuffix;
        string serialized = TomlSerializer.Serialize(root);
        await File.WriteAllTextAsync(tmp, serialized, ct);
        File.Move(tmp, mConfigPath, overwrite: true);
    }

    private static void ApplyEntry(TomlTable root, SaddleRagEndpoint endpoint)
    {
        TomlTable features = GetOrAddTable(root, FeaturesTable);
        features[RmcpFlag] = true;

        TomlTable servers = GetOrAddTable(root, McpServersTable);
        TomlTable entry = new()
                              {
                                  [UrlKey] = endpoint.Url
                              };
        servers[SaddleRagKey] = entry;
    }

    private static bool RemoveEntry(TomlTable root)
    {
        bool removed = false;
        if (root.TryGetValue(McpServersTable, out object? serversObj) && serversObj is TomlTable servers
            && servers.ContainsKey(SaddleRagKey))
        {
            servers.Remove(SaddleRagKey);
            removed = true;
        }
        return removed;
    }

    private static TomlTable GetOrAddTable(TomlTable parent, string key)
    {
        TomlTable res;
        if (parent.TryGetValue(key, out object? existing) && existing is TomlTable table)
            res = table;
        else
        {
            res = new TomlTable();
            parent[key] = res;
        }
        return res;
    }
}
