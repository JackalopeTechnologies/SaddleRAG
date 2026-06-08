// SkillCatalog.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Reflection;
using System.Text;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.ClientIntegration.Skills;

/// <summary>
///     The SaddleRAG skill documents (saddlerag-first, -query, -recon, -scrape,
///     -scrape-strategy, -maintain) parsed from the embedded Resources. Exposed so a host
///     that cannot install skill files — notably the MCP server surfacing them as prompts
///     to Claude Desktop and other MCP-only clients — can serve the same full content.
///     Skill-capable clients keep installing these as files via the client writers; this is
///     the same single source.
/// </summary>
public static class SkillCatalog
{
    private const string FrontmatterFence = "---";
    private const string NameKey = "name:";
    private const string DescriptionKey = "description:";
    private const char NewLine = '\n';
    private const string WindowsNewLine = "\r\n";
    private const string UnixNewLine = "\n";

    private static readonly SkillContent[] smAll = Load();

    public static IReadOnlyList<SkillContent> All => smAll;

    private static SkillContent[] Load()
    {
        Assembly asm = typeof(SkillCatalog).Assembly;
        List<SkillContent> res = [];
        foreach (SkillDescriptor descriptor in SkillManifest.pmAll)
        {
            string markdown = ReadResource(asm, descriptor.ResourceName);
            res.Add(Parse(descriptor.FolderName, markdown));
        }
        return res.ToArray();
    }

    private static string ReadResource(Assembly asm, string resourceName)
    {
        using Stream stream = asm.GetManifestResourceStream(resourceName)
                              ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static SkillContent Parse(string fallbackName, string markdown)
    {
        // Skill files lead with a YAML frontmatter block fenced by '---'. Pull name and
        // description from it for the prompt metadata; the body (everything after the
        // closing fence) becomes the prompt content. Files without frontmatter fall back
        // to the folder name and the whole document as body.
        string name = fallbackName;
        string description = string.Empty;
        string body = markdown.Trim();

        string[] lines = markdown.Replace(WindowsNewLine, UnixNewLine).Split(NewLine);
        bool hasFrontmatter = lines.Length > 0 && lines[0].Trim() == FrontmatterFence;
        int closeIndex = hasFrontmatter ? FindClosingFence(lines) : -1;
        if (closeIndex > 0)
        {
            ReadFrontmatter(lines, closeIndex, ref name, ref description);
            body = string.Join(UnixNewLine, lines.Skip(closeIndex + 1)).Trim();
        }

        return new SkillContent(name, description, body);
    }

    private static int FindClosingFence(string[] lines)
    {
        int res = -1;
        for (int i = 1; i < lines.Length && res < 0; i++)
        {
            if (lines[i].Trim() == FrontmatterFence)
                res = i;
        }
        return res;
    }

    private static void ReadFrontmatter(string[] lines, int closeIndex, ref string name, ref string description)
    {
        // Independent (non-chained) checks: a single line never matches both keys.
        for (int i = 1; i < closeIndex; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith(NameKey, StringComparison.Ordinal))
                name = line[NameKey.Length..].Trim();
            if (line.StartsWith(DescriptionKey, StringComparison.Ordinal))
                description = line[DescriptionKey.Length..].Trim();
        }
    }
}
