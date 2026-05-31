// CursorWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.ClientIntegration.Writers;

public sealed class CursorWriter : IClientWriter
{
    #region Constants

    private const string Name = "cursor";
    private const string SaddleRagKey = "saddlerag";
    private const string McpServersKey = "mcpServers";
    private const string UrlKey = "url";
    private const string CursorDir = ".cursor";
    private const string ConfigFile = "mcp.json";
    private const string TmpSuffix = ".tmp";

    private const string MsgRegisterDidNotRun = "register did not run";
    private const string MsgRegistered = "registered";
    private const string MsgConfigFileMissing = "config file does not exist";
    private const string MsgSaddleRagRemoved = "saddlerag entry removed";
    private const string MsgSaddleRagNotPresent = "saddlerag entry was not present";

    #endregion

    private static readonly JsonSerializerOptions smWriteOptions = new()
                                                                       {
                                                                           WriteIndented = true
                                                                       };

    private static readonly UTF8Encoding smUtf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string mConfigPath;

    public CursorWriter(string configPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        mConfigPath = configPath;
    }

    public string ClientName => Name;

    public string ConfigPath => mConfigPath;

    public bool IsDetected()
    {
        string? dir = Path.GetDirectoryName(mConfigPath);
        return dir is not null && Directory.Exists(dir);
    }

    public static CursorWriter ForCurrentUser()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string config = Path.Combine(profile, CursorDir, ConfigFile);
        return new CursorWriter(config);
    }

    public async Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        RegisterResult res = RegisterResult.Failed(Name, mConfigPath, MsgRegisterDidNotRun);
        try
        {
            JsonObject root = await ReadOrNewRootAsync(ct);
            JsonObject servers = (root[McpServersKey] as JsonObject) ?? new JsonObject();
            servers[SaddleRagKey] = new JsonObject
                                        {
                                            [UrlKey] = endpoint.Url
                                        };
            root[McpServersKey] = servers;
            await SaveRootAsync(root, ct);
            res = RegisterResult.Ok(Name, mConfigPath, MsgRegistered, string.Empty);
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
                JsonObject root = await ReadOrNewRootAsync(ct);
                bool removed = RemoveSaddleRagEntry(root);
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
        string notes = string.Empty;
        if (fileExists)
        {
            try
            {
                JsonObject root = await ReadOrNewRootAsync(ct);
                JsonObject? servers = root[McpServersKey] as JsonObject;
                JsonObject? entry = servers?[SaddleRagKey] as JsonObject;
                string? url = entry?[UrlKey]?.GetValue<string>();
                entryPresent = url is not null;
                endpointMatches = string.Equals(url, SaddleRagEndpoint.Default.Url, StringComparison.Ordinal);
            }
            catch (JsonException ex)
            {
                notes = $"config file malformed: {ex.Message}";
            }
        }
        return new StatusResult(Name, mConfigPath, fileExists, entryPresent, endpointMatches, null, notes);
    }

    private async Task<JsonObject> ReadOrNewRootAsync(CancellationToken ct)
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
}
