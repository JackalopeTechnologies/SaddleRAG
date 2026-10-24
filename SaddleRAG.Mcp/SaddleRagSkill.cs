// SaddleRagSkill.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SaddleRAG.ClientIntegration.Skills;

namespace SaddleRAG.Mcp;

/// <summary>One immutable embedded skill snapshot and its complete file manifest.</summary>
internal sealed class SaddleRagSkill
{
    private readonly JsonObject mManifest;

    public SaddleRagSkill(SkillContent skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        Name = skill.Name;
        Description = skill.Description;
        Uri = GetFileUri(SkillProtocolNames.SkillFile);
        Files = SkillCatalog.GetFiles(Name);
        JsonArray resources = [];
        foreach ((string path, string content) in Files)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            resources.Add(new JsonObject
                              {
                                  [SkillProtocolNames.Uri] = GetFileUri(path),
                                  [SkillProtocolNames.Digest] = SkillProtocolNames.DigestPrefix + Convert.ToHexStringLower(SHA256.HashData(bytes)),
                                  [SkillProtocolNames.Size] = bytes.Length
                              });
        }

        mManifest = new JsonObject
                        {
                            [SkillProtocolNames.Uri] = Uri,
                            [SkillProtocolNames.Frontmatter] = SkillCatalog.GetFrontmatter(Name),
                            [SkillProtocolNames.Resources] = resources
                        };
    }

    public string Name { get; }
    public string Description { get; }
    public string Uri { get; }
    public IReadOnlyDictionary<string, string> Files { get; }

    public string GetFileUri(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return $"{SkillProtocolNames.UriPrefix}{Name}/{path}";
    }

    public JsonNode GetManifest()
    {
        return mManifest.DeepClone();
    }
}
