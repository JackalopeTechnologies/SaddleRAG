// ScrapeJobTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Models;

public sealed class ScrapeJobTests
{
    // ContentSelector is a per-scrape override for WHERE the extractor reads content; it wins
    // over WaitForSelector, whose job is render timing.
    [Fact]
    public void EffectiveContentSelectorPrefersContentSelector()
    {
        var job = MakeJob() with { ContentSelector = ".docs-container", WaitForSelector = ".mud-main-content" };

        Assert.Equal(".docs-container", job.EffectiveContentSelector);
    }

    // When no explicit ContentSelector is given, the extractor still tries WaitForSelector first,
    // preserving the existing behavior.
    [Fact]
    public void EffectiveContentSelectorFallsBackToWaitForSelector()
    {
        var job = MakeJob() with { ContentSelector = null, WaitForSelector = ".mud-main-content" };

        Assert.Equal(".mud-main-content", job.EffectiveContentSelector);
    }

    [Fact]
    public void EffectiveContentSelectorNullWhenNeitherSet()
    {
        var job = MakeJob();

        Assert.Null(job.EffectiveContentSelector);
    }

    private static ScrapeJob MakeJob() =>
        new()
            {
                RootUrl = "https://example.com",
                LibraryHint = "example",
                LibraryId = "example",
                Version = "1.0",
                AllowedUrlPatterns = ["example.com"]
            };
}
