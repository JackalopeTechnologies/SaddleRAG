// SaddleRagSkills.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using SaddleRAG.ClientIntegration.Skills;

namespace SaddleRAG.Mcp;

/// <summary>Read-only discovery of packaged skills; never reads paths supplied by a client.</summary>
internal static class SaddleRagSkills
{
    internal const string ExtensionName = "io.modelcontextprotocol/skills";
    private const string SkillsProtocolVersion = "2026-07-28";
    private const string MarkdownMimeType = "text/markdown";
    private const int CacheLifetimeMilliseconds = 300000;
    private const string MissingSkillUri = "A skill URI is required.";
    private const string UnknownSkillUri = "Unknown SaddleRAG skill URI.";
    private const string InvalidResourceCursor = "This resource listing has no continuation cursor.";
    private const string MissingResourceUri = "A resource URI is required.";
    private const string UnknownResourceUri = "Unknown SaddleRAG resource URI.";
    private const string UnsupportedSkillsVersion = "Skills discovery requires MCP 2026-07-28.";
    private const string InvalidParameters = "Request parameters must be an object.";
    private const string InvalidSkillCursor = "This skill listing has no continuation cursor.";
    private static readonly SaddleRagSkill[] smSkills = SkillCatalog.All.Select(static skill => new SaddleRagSkill(skill)).ToArray();

    internal static int Count => smSkills.Length;

    public static ValueTask<JsonNode?> ListAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request, cancellationToken);
        ValidateCursor(request.Params?[SkillProtocolNames.Cursor]);
        JsonArray skills = [];
        foreach (SaddleRagSkill skill in smSkills)
            skills.Add(skill.GetManifest());

        JsonObject result = CreateResult();
        result[SkillProtocolNames.Skills] = skills;
        return ValueTask.FromResult<JsonNode?>(result);
    }

    public static ValueTask<JsonNode?> GetAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request, cancellationToken);
        JsonNode? uriNode = request.Params?[SkillProtocolNames.Uri];
        if (uriNode is not JsonValue uriValue || !uriValue.TryGetValue(out string? uri) || string.IsNullOrWhiteSpace(uri))
            throw new McpProtocolException(MissingSkillUri, McpErrorCode.InvalidParams);

        SaddleRagSkill? skill = smSkills.FirstOrDefault(item => string.Equals(item.Uri, uri, StringComparison.Ordinal));
        if (skill is null)
            throw new McpProtocolException(UnknownSkillUri, McpErrorCode.InvalidParams);

        JsonObject result = CreateResult();
        result[SkillProtocolNames.Skill] = skill.GetManifest();
        return ValueTask.FromResult<JsonNode?>(result);
    }

    public static ListResourcesResult ListResources(string? cursor)
    {
        if (cursor is not null)
            throw new McpProtocolException(InvalidResourceCursor, McpErrorCode.InvalidParams);

        List<Resource> resources = [];
        foreach (SaddleRagSkill skill in smSkills)
        {
            foreach (string path in skill.Files.Keys)
            {
                resources.Add(new Resource
                                  {
                                      Uri = skill.GetFileUri(path),
                                      Name = path == SkillProtocolNames.SkillFile ? skill.Name : path,
                                      Description = skill.Description,
                                      MimeType = MarkdownMimeType
                                  });
            }
        }

        // The SDK supplies modern result/cache fields only for clients using that revision.
        return new ListResourcesResult { Resources = resources };
    }

    public static ReadResourceResult ReadResource(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new McpProtocolException(MissingResourceUri, McpErrorCode.InvalidParams);

        TextResourceContents? content = null;
        foreach (SaddleRagSkill skill in smSkills)
        {
            foreach ((string path, string text) in skill.Files)
            {
                if (string.Equals(skill.GetFileUri(path), uri, StringComparison.Ordinal))
                    content = new TextResourceContents { Uri = uri, MimeType = MarkdownMimeType, Text = text };
            }
        }

        if (content is null)
            throw new McpProtocolException(UnknownResourceUri, McpErrorCode.InvalidParams);

        return new ReadResourceResult { Contents = [content] };
    }

    private static void ValidateRequest(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        // Context is validated by the transport. Future metadata on a legacy request is opaque.
        if (!string.Equals(request.Context?.ProtocolVersion, SkillsProtocolVersion, StringComparison.Ordinal))
            throw new McpProtocolException(UnsupportedSkillsVersion, McpErrorCode.MethodNotFound);

        if (request.Params is not null and not JsonObject)
            throw new McpProtocolException(InvalidParameters, McpErrorCode.InvalidParams);
    }

    private static void ValidateCursor(JsonNode? cursor)
    {
        if (cursor is not null)
            throw new McpProtocolException(InvalidSkillCursor, McpErrorCode.InvalidParams);
    }

    private static JsonObject CreateResult()
    {
        return new JsonObject
                   {
                       [SkillProtocolNames.ResultType] = SkillProtocolNames.Complete,
                       [SkillProtocolNames.TtlMs] = CacheLifetimeMilliseconds,
                       [SkillProtocolNames.CacheScope] = SkillProtocolNames.Public
                   };
    }
}
