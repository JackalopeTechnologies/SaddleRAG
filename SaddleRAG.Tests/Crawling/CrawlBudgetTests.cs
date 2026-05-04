// CrawlBudgetTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Crawling;

public sealed class CrawlBudgetTests
{
    [Fact]
    public void GetLimiterReturnsSameInstancePerBucket()
    {
        var budget = new CrawlBudget();

        var first = budget.GetLimiter(new Uri("https://docs.example.com/"));
        var second = budget.GetLimiter(new Uri("https://docs.example.com/other/path"));
        var other = budget.GetLimiter(new Uri("https://api.example.com/"));

        Assert.Same(first, second);
        Assert.NotSame(first, other);
        Assert.Equal(expected: 2, budget.HostCount);
    }

    [Fact]
    public void HostLookupIsCaseInsensitive()
    {
        var budget = new CrawlBudget();

        var lower = budget.GetLimiter(new Uri("https://docs.example.com/"));
        var upper = budget.GetLimiter(new Uri("https://DOCS.EXAMPLE.COM/"));

        Assert.Same(lower, upper);
        Assert.Equal(expected: 1, budget.HostCount);
    }

    [Fact]
    public void GetLimiterSeparatesBucketsByScheme()
    {
        var budget = new CrawlBudget();

        var http = budget.GetLimiter(new Uri("http://docs.example.com/"));
        var https = budget.GetLimiter(new Uri("https://docs.example.com/"));

        Assert.NotSame(http, https);
        Assert.Equal(expected: 2, budget.HostCount);
    }

    [Fact]
    public void GetLimiterAcceptsFileUriWithEmptyHost()
    {
        var budget = new CrawlBudget();

        var first = budget.GetLimiter(new Uri("file:///E:/docs/index.htm"));
        var second = budget.GetLimiter(new Uri("file:///E:/docs/other.htm"));

        Assert.Same(first, second);
        Assert.Equal(expected: 1, budget.HostCount);
    }

    [Fact]
    public void GetScopeFilterReturnsSameInstancePerBucket()
    {
        var budget = new CrawlBudget();

        var first = budget.GetScopeFilter(new Uri("https://docs.example.com/"));
        var second = budget.GetScopeFilter(new Uri("https://docs.example.com/page"));
        var other = budget.GetScopeFilter(new Uri("https://api.example.com/"));

        Assert.Same(first, second);
        Assert.NotSame(first, other);
    }

    [Fact]
    public void GetScopeFilterIsCaseInsensitive()
    {
        var budget = new CrawlBudget();

        var lower = budget.GetScopeFilter(new Uri("https://docs.example.com/"));
        var upper = budget.GetScopeFilter(new Uri("https://DOCS.EXAMPLE.COM/"));

        Assert.Same(lower, upper);
    }

    [Fact]
    public void GetScopeFilterAcceptsFileUriWithEmptyHost()
    {
        var budget = new CrawlBudget();

        var first = budget.GetScopeFilter(new Uri("file:///E:/docs/index.htm"));
        var second = budget.GetScopeFilter(new Uri("file:///E:/docs/other.htm"));

        Assert.Same(first, second);
    }

    [Fact]
    public void BuildHostKeyProducesSchemeAndHost()
    {
        Assert.Equal("https://docs.example.com",
                     CrawlBudget.BuildHostKey(new Uri("https://docs.example.com/some/path"))
                    );
        Assert.Equal("http://docs.example.com",
                     CrawlBudget.BuildHostKey(new Uri("http://docs.example.com/"))
                    );
        Assert.Equal("file://",
                     CrawlBudget.BuildHostKey(new Uri("file:///E:/docs/index.htm"))
                    );
    }

    [Fact]
    public void GetSnapshotReturnsCurrentConcurrencyPerBucket()
    {
        var budget = new CrawlBudget(initialConcurrency: 4, minConcurrency: 1, maxConcurrency: 8);

        var docsLimiter = budget.GetLimiter(new Uri("https://docs.example.com/"));
        budget.GetLimiter(new Uri("https://api.example.com/"));

        docsLimiter.ReportRateLimited(TimeSpan.Zero);

        var snapshot = budget.GetSnapshot();

        Assert.Equal(expected: 2, snapshot["https://docs.example.com"]);
        Assert.Equal(expected: 4, snapshot["https://api.example.com"]);
    }

    [Fact]
    public void ParseRetryAfterAcceptsDeltaSeconds()
    {
        var result = CrawlBudget.ParseRetryAfter("30");

        Assert.Equal(TimeSpan.FromSeconds(seconds: 30), result);
    }

    [Fact]
    public void ParseRetryAfterAcceptsHttpDate()
    {
        var future = DateTime.UtcNow.AddMinutes(value: 2);
        var headerValue = future.ToString("R");

        var result = CrawlBudget.ParseRetryAfter(headerValue);

        Assert.NotNull(result);
        Assert.True(result.Value > TimeSpan.FromSeconds(seconds: 60));
        Assert.True(result.Value < TimeSpan.FromMinutes(minutes: 3));
    }

    [Theory]
    [InlineData(data: null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-30")]
    public void ParseRetryAfterReturnsNullForInvalidInput(string? input)
    {
        var result = CrawlBudget.ParseRetryAfter(input);

        Assert.Null(result);
    }

    [Fact]
    public void ParseRetryAfterReturnsNullForPastDate()
    {
        var past = DateTime.UtcNow.AddMinutes(value: -5).ToString("R");

        var result = CrawlBudget.ParseRetryAfter(past);

        Assert.Null(result);
    }
}
