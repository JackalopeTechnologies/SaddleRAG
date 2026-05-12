// IngestToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class IngestToolsTests
{
    [Fact]
    public void MakeReadyToScrapeIncludesExcludedPatternsWhenSupplied()
    {
        var response = IngestTools.MakeReadyToScrape("foo",
                                                     "1.0",
                                                     "https://docs.example.com",
                                                     "ready",
                                                         ["/account/login", "/account/register"]
                                                    );

        Assert.Equal(IngestStatus.ReadyToScrape, response.Status);
        Assert.Equal(["/account/login", "/account/register"], response.RecommendedExcludedUrlPatterns);
    }

    [Fact]
    public void MakeReadyToScrapeHandlesEmptyPatterns()
    {
        var response = IngestTools.MakeReadyToScrape("foo",
                                                     "1.0",
                                                     "https://docs.example.com",
                                                     "ready",
                                                         []
                                                    );

        Assert.Empty(response.RecommendedExcludedUrlPatterns);
    }

    [Fact]
    public void MakeReadyToScrapePopulatesNextToolArgsForScrapeDocs()
    {
        var response = IngestTools.MakeReadyToScrape("foo",
                                                     "1.0",
                                                     "https://docs.example.com",
                                                     "ready",
                                                         ["/account/login"]
                                                    );

        Assert.Equal("scrape_docs", response.NextTool);
        Assert.Equal("https://docs.example.com", response.NextToolArgs["url"]);
        Assert.Equal("foo", response.NextToolArgs["libraryId"]);
        Assert.Equal("1.0", response.NextToolArgs["version"]);
    }
}
