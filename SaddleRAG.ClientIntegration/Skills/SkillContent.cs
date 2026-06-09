// SkillContent.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.ClientIntegration.Skills;

/// <summary>
///     One SaddleRAG skill document (name, one-line description, and body) parsed from
///     its embedded markdown resource. Name and Body are guaranteed non-empty; Description
///     may be empty (a skill resource without a description: frontmatter line).
/// </summary>
public sealed record SkillContent
{
    public SkillContent(string name, string description, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        Name = name;
        Description = description;
        Body = body;
    }

    public string Name { get; }
    public string Description { get; }
    public string Body { get; }
}
