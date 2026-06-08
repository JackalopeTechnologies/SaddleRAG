// SaddleRagPrompts.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using ModelContextProtocol.Protocol;
using SaddleRAG.ClientIntegration.Skills;

#endregion

namespace SaddleRAG.Mcp;

/// <summary>
///     Surfaces the SaddleRAG skill documents as MCP prompts so clients that cannot install
///     skill files (Claude Desktop and other MCP-only hosts) can still pull up the full
///     recon / scrape / scrape-strategy / maintain / query / first guidance, not just the
///     condensed ServerInstructions summary. Skill-capable clients keep getting the same
///     documents as installed skill files via the client writers — this is the same source
///     (<see cref="SkillCatalog" />).
/// </summary>
internal static class SaddleRagPrompts
{
    private const string UnknownPromptPrefix = "Unknown SaddleRAG prompt: ";

    public static ListPromptsResult List()
    {
        List<Prompt> prompts = SkillCatalog.All
                                           .Select(static s => new Prompt
                                                                   {
                                                                       Name = s.Name,
                                                                       Description = s.Description
                                                                   })
                                           .ToList();
        return new ListPromptsResult { Prompts = prompts };
    }

    public static GetPromptResult Get(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentNullException(nameof(name));

        SkillContent? skill = SkillCatalog.All
                                          .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (skill is null)
            throw new ArgumentException($"{UnknownPromptPrefix}{name}", nameof(name));

        return new GetPromptResult
                   {
                       Description = skill.Description,
                       Messages =
                       [
                           new PromptMessage
                               {
                                   Role = Role.User,
                                   Content = new TextContentBlock { Text = skill.Body }
                               }
                       ]
                   };
    }
}
