// PageRepositoryBuildUpsertFilterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>
///     Pins the PageRepository.BuildUpsertFilter contract: the upsert filter
///     MUST target the synthetic <c>_id</c> (canonical URL hash), not the
///     raw <see cref="PageRecord.Url" />. If this regresses, the upsert
///     filter for <c>/Build.html</c> misses the existing record stored at
///     <c>/Build</c>, the upsert switches to insert, and Mongo rejects the
///     duplicate canonical <c>_id</c> — silently losing the rescrape of
///     every page that gets served under more than one URL variant.
/// </summary>
public sealed class PageRepositoryBuildUpsertFilterTests
{
    [Fact]
    public void BuildUpsertFilterTargetsIdField()
    {
        var page = MakePage(id: "lib1/current/abc123def456",
                            libraryId: "lib1",
                            version: "current",
                            url: "https://example.com/Build.html"
                           );

        var filter = PageRepository.BuildUpsertFilter(page);
        BsonDocument rendered = RenderToBson(filter);

        Assert.True(rendered.Contains("_id"));
        Assert.False(rendered.Contains("Url"));
        Assert.False(rendered.Contains("LibraryId"));
        Assert.False(rendered.Contains("Version"));
    }

    [Fact]
    public void BuildUpsertFilterMatchesAnotherPageWithSameIdButDifferentUrl()
    {
        const string sharedId = "lib1/current/abc123def456";

        var pageA = MakePage(id: sharedId,
                             libraryId: "lib1",
                             version: "current",
                             url: "https://example.com/Build"
                            );
        var pageB = MakePage(id: sharedId,
                             libraryId: "lib1",
                             version: "current",
                             url: "https://example.com/Build.html"
                            );

        BsonDocument renderedA = RenderToBson(PageRepository.BuildUpsertFilter(pageA));
        BsonDocument renderedB = RenderToBson(PageRepository.BuildUpsertFilter(pageB));

        Assert.Equal(renderedA, renderedB);
    }

    [Fact]
    public void BuildUpsertFilterEmitsExactCanonicalIdValue()
    {
        var page = MakePage(id: "lib1/current/abc123def456",
                            libraryId: "lib1",
                            version: "current",
                            url: "https://example.com/Build.html"
                           );

        BsonDocument rendered = RenderToBson(PageRepository.BuildUpsertFilter(page));

        Assert.Equal("lib1/current/abc123def456", rendered["_id"].AsString);
    }

    private static PageRecord MakePage(string id, string libraryId, string version, string url)
    {
        var page = new PageRecord
                       {
                           Id = id,
                           LibraryId = libraryId,
                           Version = version,
                           Url = url,
                           Title = "Test Page",
                           Category = DocCategory.Unclassified,
                           RawContent = "content",
                           FetchedAt = DateTime.UtcNow,
                           ContentHash = "hash"
                       };
        return page;
    }

    private static BsonDocument RenderToBson(FilterDefinition<PageRecord> filter)
    {
        var documentSerializer = BsonSerializer.SerializerRegistry.GetSerializer<PageRecord>();
        var rendered = filter.Render(new RenderArgs<PageRecord>(documentSerializer, BsonSerializer.SerializerRegistry));
        return rendered;
    }
}
