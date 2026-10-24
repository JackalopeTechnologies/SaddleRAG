// SkillProtocolNames.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Mcp;

/// <summary>Wire names defined by the official MCP Skills extension.</summary>
internal static class SkillProtocolNames
{
    internal const string UriPrefix = "skill://";
    internal const string SkillFile = "SKILL.md";
    internal const string Uri = "uri";
    internal const string Digest = "digest";
    internal const string DigestPrefix = "sha256:";
    internal const string Size = "size";
    internal const string Frontmatter = "frontmatter";
    internal const string Resources = "resources";
    internal const string Skills = "skills";
    internal const string Skill = "skill";
    internal const string Cursor = "cursor";
    internal const string ResultType = "resultType";
    internal const string Complete = "complete";
    internal const string TtlMs = "ttlMs";
    internal const string CacheScope = "cacheScope";
    internal const string Public = "public";
    internal const string ListMethod = "skills/list";
    internal const string GetMethod = "skills/get";
}
