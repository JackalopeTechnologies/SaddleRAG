// IngestStatusResponseTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Text.Json;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class IngestStatusResponseTests
{
    [Fact]
    public void StatusNameMirrorsEnumName()
    {
        var response = new IngestStatusResponse
                           {
                               Status = IngestStatus.InProgress,
                               LibraryId = "foo",
                               Version = "1.0",
                               Url = "https://example.com"
                           };

        Assert.Equal("InProgress", response.StatusName);
    }

    [Fact]
    public void SerializedJsonIncludesStatusNameAlongsideNumericStatus()
    {
        var response = new IngestStatusResponse
                           {
                               Status = IngestStatus.UrlSuspect,
                               LibraryId = "foo",
                               Version = "1.0",
                               Url = "https://example.com"
                           };

        var json = JsonSerializer.Serialize(response);

        Assert.Contains("\"StatusName\":\"UrlSuspect\"", json);
        Assert.Contains("\"Status\":", json);
    }

    [Fact]
    public void RecommendedExcludedUrlPatternsDefaultsToEmpty()
    {
        var response = new IngestStatusResponse
                           {
                               Status = IngestStatus.ReadyToScrape,
                               LibraryId = "foo",
                               Version = "1.0",
                               Url = "https://example.com"
                           };

        Assert.NotNull(response.RecommendedExcludedUrlPatterns);
        Assert.Empty(response.RecommendedExcludedUrlPatterns);
    }

    [Fact]
    public void RecommendedExcludedUrlPatternsSerializesToJsonArray()
    {
        var response = new IngestStatusResponse
                           {
                               Status = IngestStatus.ReadyToScrape,
                               LibraryId = "foo",
                               Version = "1.0",
                               Url = "https://example.com",
                               RecommendedExcludedUrlPatterns = ["/account/login", "/account/register"]
                           };

        var json = JsonSerializer.Serialize(response);

        Assert.Contains("\"RecommendedExcludedUrlPatterns\":[\"/account/login\",\"/account/register\"]",
                        json
                       );
    }
}
