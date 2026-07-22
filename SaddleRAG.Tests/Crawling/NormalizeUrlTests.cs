// NormalizeUrlTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Reflection;
using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Crawling;

public sealed class NormalizeUrlTests
{
    // NormalizeUrl is private static — expose via reflection for testing.
    private static string? Invoke(string url, bool keepExtension = false)
    {
        var method = typeof(PageCrawler).GetMethod("NormalizeUrl",
                                                   BindingFlags.NonPublic | BindingFlags.Static
                                                  );
        if (method == null)
            throw new InvalidOperationException("NormalizeUrl method not found on PageCrawler.");
        return (string?) method.Invoke(obj: null, [url, keepExtension]);
    }

    [Fact]
    public void NonDefaultPortIsPreservedAfterNormalization()
    {
        string? result = Invoke("http://localhost:8080/docs/page.html");
        Assert.Equal("http://localhost:8080/docs/page", result);
    }

    [Fact]
    public void DefaultHttpPortIsNotAppended()
    {
        string? result = Invoke("http://example.com:80/docs/page.html");
        Assert.Equal("http://example.com/docs/page", result);
    }

    [Fact]
    public void DefaultHttpsPortIsNotAppended()
    {
        string? result = Invoke("https://example.com:443/docs/page");
        Assert.Equal("https://example.com/docs/page", result);
    }

    [Fact]
    public void KeepExtensionPreservesDocumentExtensionForFetch()
    {
        string? result = Invoke("https://www.advancedinstaller.com/user-guide/modules-project-tab.html",
                                keepExtension: true
                               );

        Assert.Equal("https://www.advancedinstaller.com/user-guide/modules-project-tab.html", result);
    }
}
