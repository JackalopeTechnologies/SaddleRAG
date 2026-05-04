// SuspectDetectorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Ingestion.Suspect;

#endregion

namespace SaddleRAG.Tests.Suspect;

public sealed class SuspectDetectorTests
{
    [Fact]
    public async Task OnePagerFlagsBelowThreshold()
    {
        var d = new SuspectDetector();
        var reasons = await d.EvaluateAsync("lib",
                                            "1.0",
                                            "https://example.com",
                                            pageCount: 2,
                                            distinctHostCount: 1,
                                            distinctLinkTargets: 50,
                                            new Dictionary<string, double> { ["csharp"] = 1.0 },
                                            new[] { "csharp" },
                                            new[] { "About" },
                                            TestContext.Current.CancellationToken
                                           );

        Assert.Contains(SuspectReason.OnePager, reasons);
    }

    [Fact]
    public async Task LanguageMismatchFlagsWhenNoDeclaredLanguageAboveThreshold()
    {
        var d = new SuspectDetector();
        var reasons = await d.EvaluateAsync("lib",
                                            "1.0",
                                            "https://example.com",
                                            pageCount: 100,
                                            distinctHostCount: 1,
                                            distinctLinkTargets: 50,
                                            new Dictionary<string, double> { ["go"] = 0.5, ["ruby"] = 0.5 },
                                            new[] { "csharp" },
                                            new[] { "Some doc" },
                                            TestContext.Current.CancellationToken
                                           );

        Assert.Contains(SuspectReason.LanguageMismatch, reasons);
    }

    [Fact]
    public async Task HealthyLibraryNoReasons()
    {
        var d = new SuspectDetector();
        var reasons = await d.EvaluateAsync("lib",
                                            "1.0",
                                            "https://docs.example.com",
                                            pageCount: 500,
                                            distinctHostCount: 3,
                                            distinctLinkTargets: 1000,
                                            new Dictionary<string, double> { ["csharp"] = 0.9 },
                                            new[] { "csharp" },
                                            new[] { "Tutorial", "Reference" },
                                            TestContext.Current.CancellationToken
                                           );

        Assert.Empty(reasons);
    }

    [Fact]
    public async Task ReadmeOnlyFlagsGitHubRootWithReadmeTitles()
    {
        var d = new SuspectDetector();
        var reasons = await d.EvaluateAsync("lib",
                                            "1.0",
                                            "https://github.com/foo/bar",
                                            pageCount: 1,
                                            distinctHostCount: 1,
                                            distinctLinkTargets: 50,
                                            new Dictionary<string, double> { ["csharp"] = 1.0 },
                                            new[] { "csharp" },
                                            new[] { "README - foo/bar" },
                                            TestContext.Current.CancellationToken
                                           );

        Assert.Contains(SuspectReason.ReadmeOnly, reasons);
    }

    [Fact]
    public async Task SparseLinkGraphFlagsBelowThreshold()
    {
        var d = new SuspectDetector();
        var reasons = await d.EvaluateAsync("lib",
                                            "1.0",
                                            "https://docs.example.com",
                                            pageCount: 100,
                                            distinctHostCount: 2,
                                            distinctLinkTargets: 5,
                                            new Dictionary<string, double> { ["csharp"] = 0.9 },
                                            new[] { "csharp" },
                                            new[] { "Tutorial" },
                                            TestContext.Current.CancellationToken
                                           );

        Assert.Contains(SuspectReason.SparseLinkGraph, reasons);
    }

    /// <summary>
    ///     Frozen capture of the runaway mongodb.driver 3.4.0 scrape that prompted
    ///     this branch. The NuGet PackageProjectUrl resolved to the multi-language
    ///     MongoDB docs landing page, causing the crawler to index mostly Go/Ruby
    ///     content under a declared C# library. LanguageMismatch must fire; the other
    ///     heuristics must not — the link graph and page count are fine for a real
    ///     docs portal.
    /// </summary>
    [Fact]
    public async Task MongoDbDriverCanonicalBugFlagsLanguageMismatch()
    {
        var d = new SuspectDetector();
        var reasons = await d.EvaluateAsync("mongodb.driver",
                                            "3.4.0",
                                            "https://www.mongodb.com/docs/drivers/",
                                            pageCount: 1018,
                                            distinctHostCount: 4,
                                            distinctLinkTargets: 800,
                                            new Dictionary<string, double>
                                                {
                                                    ["go"] = 0.40,
                                                    ["ruby"] = 0.30,
                                                    ["python"] = 0.20,
                                                    ["javascript"] = 0.05,
                                                    ["csharp"] = 0.05
                                                },
                                            new[] { "csharp" },
                                            new[]
                                                {
                                                    "MongoDB Go Driver", "MongoDB Ruby Driver",
                                                    "MongoDB Python Driver"
                                                },
                                            TestContext.Current.CancellationToken
                                           );

        Assert.Contains(SuspectReason.LanguageMismatch, reasons);

        // Page count (1018) is well above OnePager threshold. Distinct links (800) are above
        // SparseLinkGraph threshold. Root URL is not a GitHub repo. These must not fire.
        Assert.DoesNotContain(SuspectReason.OnePager, reasons);
        Assert.DoesNotContain(SuspectReason.SparseLinkGraph, reasons);
        Assert.DoesNotContain(SuspectReason.ReadmeOnly, reasons);
    }
}
