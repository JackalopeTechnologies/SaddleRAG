// ShouldRecoverWithExtensionTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License in the repo root.

#region Usings

using System.Reflection;
using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Crawling;

/// <summary>
///     Verifies which failed statuses earn an extension-recovery probe. The crawler
///     strips .html/.htm/.aspx when normalizing discovered links, so the URL it first
///     fetches is its own guess; recovery re-appends each known extension and latches
///     the winner for the rest of the crawl. Gating that probe on 404 alone silently
///     lost whole sites: www.advancedinstaller.com answers the extension-stripped form
///     with 405, so the latch never set and all 1,485 user-guide pages failed while the
///     real .html URLs served fine in a browser.
/// </summary>
public sealed class ShouldRecoverWithExtensionTests
{
    private static bool Invoke(int status)
    {
        var method = typeof(PageCrawler).GetMethod("ShouldRecoverWithExtension",
                                                   BindingFlags.NonPublic | BindingFlags.Static
                                                  );
        if (method == null)
            throw new InvalidOperationException("ShouldRecoverWithExtension method not found on PageCrawler.");
        object? raw = method.Invoke(obj: null, [status]);
        if (raw is not bool result)
            throw new InvalidOperationException("ShouldRecoverWithExtension returned a non-bool value.");
        return result;
    }

    // 405 is the regression this method exists for — see the type-level summary.
    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(410)]
    [InlineData(451)]
    public void ClientRejectionOfTheGuessedUrlEarnsRecovery(int status) => Assert.True(Invoke(status));

    // Auth challenges describe the caller's credentials, not the URL's shape.
    [Theory]
    [InlineData(401)]
    [InlineData(407)]
    public void AuthChallengeDoesNotEarnRecovery(int status) => Assert.False(Invoke(status));

    // Re-probing three extensions against a throttling host only adds load.
    [Fact]
    public void ThrottlingDoesNotEarnRecovery() => Assert.False(Invoke(status: 429));

    // Server-side faults say nothing about whether the extension was right.
    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void ServerErrorDoesNotEarnRecovery(int status) => Assert.False(Invoke(status));

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(304)]
    public void SuccessOrRedirectDoesNotEarnRecovery(int status) => Assert.False(Invoke(status));
}
