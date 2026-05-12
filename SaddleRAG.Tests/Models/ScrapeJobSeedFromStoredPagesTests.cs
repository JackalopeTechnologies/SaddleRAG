// ScrapeJobSeedFromStoredPagesTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Models;

public sealed class ScrapeJobSeedFromStoredPagesTests
{
    [Fact]
    public void SeedFromStoredPagesDefaultsToFalse()
    {
        var job = new ScrapeJob
                      {
                          RootUrl = TestUrl,
                          LibraryHint = TestHint,
                          LibraryId = TestLibraryId,
                          Version = TestVersion,
                          AllowedUrlPatterns = TestPatterns
                      };

        Assert.False(job.SeedFromStoredPages);
    }

    [Fact]
    public void SeedFromStoredPagesRoundTrips()
    {
        var job = new ScrapeJob
                      {
                          RootUrl = TestUrl,
                          LibraryHint = TestHint,
                          LibraryId = TestLibraryId,
                          Version = TestVersion,
                          AllowedUrlPatterns = TestPatterns,
                          SeedFromStoredPages = true
                      };

        Assert.True(job.SeedFromStoredPages);
    }

    [Fact]
    public void SeedFromStoredPagesIsIndependentOfForceClean()
    {
        var seed = new ScrapeJob
                       {
                           RootUrl = TestUrl,
                           LibraryHint = TestHint,
                           LibraryId = TestLibraryId,
                           Version = TestVersion,
                           AllowedUrlPatterns = TestPatterns,
                           SeedFromStoredPages = true
                       };
        var force = new ScrapeJob
                        {
                            RootUrl = TestUrl,
                            LibraryHint = TestHint,
                            LibraryId = TestLibraryId,
                            Version = TestVersion,
                            AllowedUrlPatterns = TestPatterns,
                            ForceClean = true
                        };

        Assert.True(seed.SeedFromStoredPages);
        Assert.False(seed.ForceClean);
        Assert.False(force.SeedFromStoredPages);
        Assert.True(force.ForceClean);
    }

    private const string TestUrl = "https://docs.example.com/";
    private const string TestHint = "example library";
    private const string TestLibraryId = "example-lib";
    private const string TestVersion = "1.0";
    private static readonly IReadOnlyList<string> TestPatterns = ["docs.example.com"];
}
