// SkillCatalogTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.ClientIntegration.Skills;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class SkillCatalogTests
{
    private static readonly string[] smExpectedSkills =
        [
            "saddlerag-first",
            "saddlerag-query",
            "saddlerag-recon",
            "saddlerag-scrape",
            "saddlerag-scrape-strategy",
            "saddlerag-maintain"
        ];

    [Fact]
    public void AllExposesEverySkillWithContent()
    {
        IReadOnlyList<SkillContent> skills = SkillCatalog.All;

        Assert.Equal(smExpectedSkills.Length, skills.Count);
        foreach (SkillContent skill in skills)
        {
            Assert.False(string.IsNullOrWhiteSpace(skill.Name), "skill name should be set");
            Assert.False(string.IsNullOrWhiteSpace(skill.Description), $"{skill.Name} description should be set");
            Assert.False(string.IsNullOrWhiteSpace(skill.Body), $"{skill.Name} body should be set");
        }
    }

    [Fact]
    public void AllContainsExactlyTheExpectedSkillNames()
    {
        string[] names = SkillCatalog.All.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        string[] expected = smExpectedSkills.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, names);
    }

    [Fact]
    public void BodyHasFrontmatterStripped()
    {
        foreach (SkillContent skill in SkillCatalog.All)
        {
            Assert.False(skill.Body.StartsWith("---", StringComparison.Ordinal),
                         $"{skill.Name} body should not retain the frontmatter fence");
            Assert.StartsWith("#", skill.Body, StringComparison.Ordinal);
        }
    }
}
