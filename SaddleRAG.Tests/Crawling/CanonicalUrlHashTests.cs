// CanonicalUrlHashTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Crawling;

/// <summary>
///     Regression coverage for <c>PageCrawler.ComputeCanonicalUrlHash</c>,
///     the helper that turns a fetched URL into a stable
///     <see cref="SaddleRAG.Core.Models.PageRecord.Id" /> seed. Before this
///     helper landed the crawler hashed the raw <c>fetchUrl</c>, which let
///     site-extension noise (<c>/Generate</c> vs <c>/Generate.html</c>)
///     produce two distinct PageRecord Ids for the same logical page.
///     The dedup contract these tests pin down is: any pair of URLs that
///     differ only in stripped extensions or trailing-slash whitespace
///     must hash to the same value.
/// </summary>
public sealed class CanonicalUrlHashTests
{
    [Fact]
    public void SameUrlWithAndWithoutHtmlExtensionHashesToSameValue()
    {
        string withExt = PageCrawler.ComputeCanonicalUrlHash("https://numerics.mathdotnet.com/Generate.html");
        string withoutExt = PageCrawler.ComputeCanonicalUrlHash("https://numerics.mathdotnet.com/Generate");

        Assert.Equal(withoutExt, withExt);
    }

    [Theory]
    [InlineData("https://example.com/docs/intro", "https://example.com/docs/intro.html")]
    [InlineData("https://example.com/docs/intro", "https://example.com/docs/intro.htm")]
    [InlineData("https://example.com/docs/intro", "https://example.com/docs/intro.aspx")]
    public void StrippedExtensionVariantsCollideWithBareForm(string bare, string withExtension)
    {
        string a = PageCrawler.ComputeCanonicalUrlHash(bare);
        string b = PageCrawler.ComputeCanonicalUrlHash(withExtension);

        Assert.Equal(a, b);
    }

    [Fact]
    public void TrailingSlashDoesNotChangeHash()
    {
        string trailing = PageCrawler.ComputeCanonicalUrlHash("https://example.com/docs/");
        string bare     = PageCrawler.ComputeCanonicalUrlHash("https://example.com/docs");

        Assert.Equal(bare, trailing);
    }

    [Fact]
    public void DifferentPathsStillProduceDifferentHashes()
    {
        string generate = PageCrawler.ComputeCanonicalUrlHash("https://numerics.mathdotnet.com/Generate");
        string matrix   = PageCrawler.ComputeCanonicalUrlHash("https://numerics.mathdotnet.com/Matrix");

        Assert.NotEqual(generate, matrix);
    }

    [Fact]
    public void QueryStringIsPreservedInTheHash()
    {
        string noQuery   = PageCrawler.ComputeCanonicalUrlHash("https://example.com/docs/page");
        string withQuery = PageCrawler.ComputeCanonicalUrlHash("https://example.com/docs/page?v=2");

        Assert.NotEqual(noQuery, withQuery);
    }

    [Fact]
    public void MalformedUrlFallsBackToHashOfRawInput()
    {
        // NormalizeUrl returns null on UriFormatException; the helper must
        // still produce *some* hash so the crawler can persist the page
        // record without crashing. The fallback is the hash of the raw
        // string, which is stable enough for the crawler's "Visited"
        // contract — malformed URLs are extremely rare in practice.
        string a = PageCrawler.ComputeCanonicalUrlHash("not a url");
        string b = PageCrawler.ComputeCanonicalUrlHash("not a url");

        Assert.Equal(a, b);
        Assert.NotEmpty(a);
    }
}
