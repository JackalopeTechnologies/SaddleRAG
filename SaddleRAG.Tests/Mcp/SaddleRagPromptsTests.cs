// SaddleRagPromptsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using ModelContextProtocol.Protocol;
using SaddleRAG.ClientIntegration.Skills;
using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class SaddleRagPromptsTests
{
    private const string ScrapeSkillName = "saddlerag-scrape";

    [Fact]
    public void ListReturnsOnePromptPerSkillWithDescriptions()
    {
        ListPromptsResult result = SaddleRagPrompts.List();

        Assert.Equal(SkillCatalog.All.Count, result.Prompts.Count);
        Assert.Contains(result.Prompts, p => p.Name == ScrapeSkillName);
        Assert.All(result.Prompts, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public void GetReturnsSkillBodyAsAUserTextMessage()
    {
        GetPromptResult result = SaddleRagPrompts.Get(ScrapeSkillName);

        Assert.False(string.IsNullOrWhiteSpace(result.Description));
        PromptMessage message = Assert.Single(result.Messages);
        Assert.Equal(Role.User, message.Role);
        TextContentBlock text = Assert.IsType<TextContentBlock>(message.Content);
        Assert.False(string.IsNullOrWhiteSpace(text.Text));
    }

    [Fact]
    public void GetWithBlankNameThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => SaddleRagPrompts.Get(null));
        Assert.Throws<ArgumentNullException>(() => SaddleRagPrompts.Get("   "));
    }

    [Fact]
    public void GetWithUnknownNameThrowsArgument()
    {
        Assert.Throws<ArgumentException>(() => SaddleRagPrompts.Get("does-not-exist"));
    }
}
