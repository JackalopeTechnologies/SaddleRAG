// MaybeApplyKnownExtensionTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Reflection;
using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Crawling;

/// <summary>
///     Verifies the URL-rewriting helper that re-attaches a discovered site
///     extension to URLs enqueued in extension-stripped form before the first
///     404+probe sequence ran. Without this, sites like help.aerotech.com that
///     require .htm on children but accept the root without it would 404 on
///     every pre-discovery URL — extension recovery only fires on the first
///     unknown-extension attempt.
/// </summary>
public sealed class MaybeApplyKnownExtensionTests
{
    private static string Invoke(string url, string? knownExtension)
    {
        var method = typeof(PageCrawler).GetMethod("MaybeApplyKnownExtension",
                                                   BindingFlags.NonPublic | BindingFlags.Static
                                                  );
        if (method == null)
            throw new InvalidOperationException("MaybeApplyKnownExtension method not found on PageCrawler.");
        object? raw = method.Invoke(obj: null, [url, knownExtension]);
        if (raw is not string result)
            throw new InvalidOperationException("MaybeApplyKnownExtension returned a non-string value.");
        return result;
    }

    [Fact]
    public void NullKnownExtensionReturnsUrlUnchanged()
    {
        var url = "https://help.aerotech.com/automation1/Content/Parameters/AbortDecelRate";
        Assert.Equal(url, Invoke(url, knownExtension: null));
    }

    [Fact]
    public void EmptyKnownExtensionReturnsUrlUnchanged()
    {
        var url = "https://help.aerotech.com/automation1/Content/Parameters/AbortDecelRate";
        Assert.Equal(url, Invoke(url, string.Empty));
    }

    [Fact]
    public void AppendsKnownExtensionToBareUrl()
    {
        string result = Invoke("https://help.aerotech.com/automation1/Content/Parameters/AbortDecelRate", ".htm");
        Assert.Equal("https://help.aerotech.com/automation1/Content/Parameters/AbortDecelRate.htm", result);
    }

    [Fact]
    public void DoesNotAppendWhenPathAlreadyHasKnownExtension()
    {
        var url = "https://help.aerotech.com/automation1/Content/Parameters.htm";
        Assert.Equal(url, Invoke(url, ".htm"));
    }

    [Fact]
    public void DoesNotAppendWhenPathHasDifferentKnownExtension()
    {
        var url = "https://docs.example.com/page.html";
        Assert.Equal(url, Invoke(url, ".htm"));
    }

    [Fact]
    public void LeavesRootPathUnchanged()
    {
        var url = "https://help.aerotech.com/";
        Assert.Equal(url, Invoke(url, ".htm"));
    }

    [Fact]
    public void LeavesEmptyPathUnchanged()
    {
        var url = "https://help.aerotech.com";
        Assert.Equal(url, Invoke(url, ".htm"));
    }

    [Fact]
    public void LeavesTrailingSlashPathUnchanged()
    {
        var url = "https://help.aerotech.com/automation1/Content/Parameters/";
        Assert.Equal(url, Invoke(url, ".htm"));
    }

    [Fact]
    public void PreservesQueryString()
    {
        string result = Invoke("https://help.aerotech.com/Content/Search?q=fault&filter=all", ".htm");
        Assert.Equal("https://help.aerotech.com/Content/Search.htm?q=fault&filter=all", result);
    }

    [Fact]
    public void PreservesNonDefaultPort()
    {
        string result = Invoke("http://localhost:8080/docs/page", ".html");
        Assert.Equal("http://localhost:8080/docs/page.html", result);
    }

    [Fact]
    public void AppendsAspxExtension()
    {
        string result = Invoke("https://docs.microsoft.com/en-us/dotnet/api/some-page", ".aspx");
        Assert.Equal("https://docs.microsoft.com/en-us/dotnet/api/some-page.aspx", result);
    }

    [Fact]
    public void MalformedUrlReturnedUnchanged()
    {
        var url = "not a url";
        Assert.Equal(url, Invoke(url, ".htm"));
    }
}
