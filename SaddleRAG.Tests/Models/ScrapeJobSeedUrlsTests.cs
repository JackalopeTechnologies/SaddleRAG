// ScrapeJobSeedUrlsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Models;

/// <summary>
///     Locks in the <see cref="ScrapeJob.SeedUrls" /> contract: defaults
///     to null, round-trips through JSON serialization unchanged, and
///     coexists with <see cref="ScrapeJob.SeedFromStoredPages" />.
///     IngestionOrchestrator unions the two sources at runtime; that
///     union is covered by the orchestrator's own integration tests.
/// </summary>
public sealed class ScrapeJobSeedUrlsTests
{
    [Fact]
    public void SeedUrlsDefaultsToNull()
    {
        var job = new ScrapeJob
                      {
                          RootUrl = TestUrl,
                          LibraryHint = TestHint,
                          LibraryId = TestLibraryId,
                          Version = TestVersion,
                          AllowedUrlPatterns = TestPatterns
                      };

        Assert.Null(job.SeedUrls);
    }

    [Fact]
    public void SeedUrlsRoundTripsThroughJson()
    {
        var seeds = new[]
                        {
                            "https://docs.example.com/api/Foo/index.htm",
                            "https://docs.example.com/api/Bar/index.htm"
                        };
        var job = new ScrapeJob
                      {
                          RootUrl = TestUrl,
                          LibraryHint = TestHint,
                          LibraryId = TestLibraryId,
                          Version = TestVersion,
                          AllowedUrlPatterns = TestPatterns,
                          SeedUrls = seeds
                      };

        string json = JsonSerializer.Serialize(job);
        var restored = JsonSerializer.Deserialize<ScrapeJob>(json);

        Assert.NotNull(restored);
        Assert.NotNull(restored.SeedUrls);
        Assert.Equal(expected: 2, restored.SeedUrls.Count);
        Assert.Equal(seeds[0], restored.SeedUrls[0]);
        Assert.Equal(seeds[1], restored.SeedUrls[1]);
    }

    [Fact]
    public void SeedUrlsAndSeedFromStoredPagesAreIndependent()
    {
        var job = new ScrapeJob
                      {
                          RootUrl = TestUrl,
                          LibraryHint = TestHint,
                          LibraryId = TestLibraryId,
                          Version = TestVersion,
                          AllowedUrlPatterns = TestPatterns,
                          SeedUrls = new[] { "https://docs.example.com/api/X/index.htm" },
                          SeedFromStoredPages = true
                      };

        Assert.NotNull(job.SeedUrls);
        Assert.True(job.SeedFromStoredPages);
    }

    [Fact]
    public void EmptySeedUrlsListIsRetained()
    {
        var job = new ScrapeJob
                      {
                          RootUrl = TestUrl,
                          LibraryHint = TestHint,
                          LibraryId = TestLibraryId,
                          Version = TestVersion,
                          AllowedUrlPatterns = TestPatterns,
                          SeedUrls = []
                      };

        Assert.NotNull(job.SeedUrls);
        Assert.Empty(job.SeedUrls);
    }

    private const string TestUrl = "https://docs.example.com";
    private const string TestHint = "Example documentation";
    private const string TestLibraryId = "example";
    private const string TestVersion = "1.0";
    private static readonly IReadOnlyList<string> TestPatterns = ["docs.example.com"];
}
