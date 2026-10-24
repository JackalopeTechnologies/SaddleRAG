// SkillDocumentTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json.Nodes;
using SaddleRAG.ClientIntegration.Skills;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class SkillDocumentTests
{
    [Fact]
    public void FilesRetainExactEmbeddedDocumentTextAndFrontmatter()
    {
        foreach (SkillDescriptor descriptor in SkillManifest.pmAll)
        {
            using Stream stream = typeof(SkillCatalog).Assembly.GetManifestResourceStream(descriptor.ResourceName)
                                  ?? throw new InvalidOperationException("Expected embedded skill document.");
            using StreamReader reader = new(stream);
            string expected = reader.ReadToEnd();
            IReadOnlyDictionary<string, string> files = SkillCatalog.GetFiles(descriptor.FolderName);
            Assert.Equal(expected, files["SKILL.md"]);
            JsonObject frontmatter = SkillCatalog.GetFrontmatter(descriptor.FolderName);
            Assert.Equal(descriptor.FolderName, frontmatter["name"]?.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(frontmatter["description"]?.GetValue<string>()));
        }
    }

    [Fact]
    public void ScrapeStrategyIncludesItsReferencedCompanionDocument()
    {
        IReadOnlyDictionary<string, string> files = SkillCatalog.GetFiles("saddlerag-scrape-strategy");
        using Stream stream = typeof(SkillCatalog).Assembly.GetManifestResourceStream(
            "SaddleRAG.ClientIntegration.Resources.case-study-infragistics-wpf.md")
            ?? throw new InvalidOperationException("Expected embedded case study.");
        using StreamReader reader = new(stream);
        Assert.Equal(reader.ReadToEnd(), files["case-study-infragistics-wpf.md"]);
        Assert.Equal(2, files.Count);
        Assert.Single(SkillCatalog.GetFiles("saddlerag-query"));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("SADDLERAG-QUERY")]
    [InlineData("../saddlerag-query")]
    [InlineData("file:///C:/secret")]
    public void FilesRejectNamesOutsideTheEmbeddedManifest(string name)
    {
        Assert.Throws<ArgumentException>(() => SkillCatalog.GetFiles(name));
        Assert.Throws<ArgumentException>(() => SkillCatalog.GetFrontmatter(name));
    }

    [Fact]
    public void FrontmatterPreservesNestedFieldsAndScalarTypes()
    {
        const string Markdown = """
            ---
            name: nested-skill
            description: >-
              A multiline description
              with a colon: and text.
            license: MIT
            metadata:
              enabled: true
              count: 3
              ratio: 1.25
              quoted: "42"
              absent: null
              tags: [one, two]
            ---
            # Skill body
            """;
        JsonObject result = SkillFrontmatter.Parse(Markdown);
        JsonNode expected = JsonNode.Parse("""
            {"name":"nested-skill","description":"A multiline description with a colon: and text.",
             "license":"MIT","metadata":{"enabled":true,"count":3,"ratio":1.25,"quoted":"42",
             "absent":null,"tags":["one","two"]}}
            """) ?? throw new InvalidOperationException("Expected JSON fixture.");
        Assert.True(JsonNode.DeepEquals(expected, result));
    }

    [Theory]
    [InlineData("---\nname: one\nname: two\ndescription: valid\n---")]
    [InlineData("---\nname: one\ndescription: valid\nmetadata: {key: one, key: two}\n---")]
    [InlineData("---\nname: 42\ndescription: valid\n---")]
    [InlineData("---\nname: one\ndescription: ''\n---")]
    [InlineData("---\nname: one\n---")]
    [InlineData("---\nname: one\ndescription: valid")]
    [InlineData("# No frontmatter")]
    public void FrontmatterRejectsAmbiguousOrIncompleteMetadata(string markdown)
    {
        Assert.Throws<InvalidDataException>(() => SkillFrontmatter.Parse(markdown));
    }

    [Fact]
    public void FrontmatterReturnsIndependentObjects()
    {
        JsonObject first = SkillCatalog.GetFrontmatter("saddlerag-query");
        first["name"] = "changed";
        Assert.Equal("saddlerag-query", SkillCatalog.GetFrontmatter("saddlerag-query")["name"]?.GetValue<string>());
    }
}
