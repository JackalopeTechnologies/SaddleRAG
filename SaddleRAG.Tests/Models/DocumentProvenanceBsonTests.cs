// DocumentProvenanceBsonTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

namespace SaddleRAG.Tests.Models;

public sealed class DocumentProvenanceBsonTests
{
    [Fact]
    public void LegacyPageAndChunkWithoutDocumentSourceDeserializeWithNullProvenance()
    {
        var page = BsonSerializer.Deserialize<PageRecord>(new BsonDocument
                                                               {
                                                                   ["_id"] = "page-1",
                                                                   ["LibraryId"] = "lib",
                                                                   ["Version"] = "v1",
                                                                   ["Url"] = "https://example.test/page",
                                                                   ["Title"] = "Legacy page",
                                                                   ["Category"] = (int) DocCategory.Overview,
                                                                   ["RawContent"] = "legacy",
                                                                   ["FetchedAt"] = DateTime.UtcNow,
                                                                   ["ContentHash"] = new string('a', 64)
                                                               });
        var chunk = BsonSerializer.Deserialize<DocChunk>(new BsonDocument
                                                              {
                                                                  ["_id"] = "chunk-1",
                                                                  ["LibraryId"] = "lib",
                                                                  ["Version"] = "v1",
                                                                  ["PageUrl"] = "https://example.test/page",
                                                                  ["PageTitle"] = "Legacy page",
                                                                  ["Category"] = (int) DocCategory.Overview,
                                                                  ["Content"] = "legacy"
                                                              });

        Assert.Null(page.DocumentSource);
        Assert.Null(chunk.DocumentSource);
    }

    [Fact]
    public void DocumentSourceRoundTripsOnPageAndChunk()
    {
        var source = new DocumentProvenance
                         {
                             DocumentId = "document-1",
                             RevisionId = "revision-1",
                             SourceUri = "saddlerag://library/lib/documents/document-1",
                             RelativePath = "manuals/guide.pdf",
                             PageStart = 4,
                             PageEnd = 5,
                             Heading = "Installation"
                         };
        var page = new PageRecord
                       {
                           Id = "page-1",
                           LibraryId = "lib",
                           Version = "v1",
                           Url = "saddlerag://library/lib/documents/document-1/sections/1",
                           Title = "Guide",
                           Category = DocCategory.HowTo,
                           RawContent = "content",
                           FetchedAt = DateTime.UtcNow,
                           ContentHash = new string('b', 64),
                           DocumentSource = source
                       };
        var chunk = new DocChunk
                        {
                            Id = "chunk-1",
                            LibraryId = "lib",
                            Version = "v1",
                            PageUrl = page.Url,
                            PageTitle = page.Title,
                            Category = page.Category,
                            Content = page.RawContent,
                            DocumentSource = source
                        };

        var restoredPage = BsonSerializer.Deserialize<PageRecord>(page.ToBson());
        var restoredChunk = BsonSerializer.Deserialize<DocChunk>(chunk.ToBson());

        Assert.Equal(source, restoredPage.DocumentSource);
        Assert.Equal(source, restoredChunk.DocumentSource);
    }

    [Fact]
    public void DocumentModelsDoNotDuplicateTheDirectoryRoot()
    {
        Assert.Null(typeof(SourceDocumentRecord).GetProperty(nameof(DirectoryLibraryDefinition.RootPath)));
        Assert.Null(typeof(DocumentRevisionRecord).GetProperty(nameof(DirectoryLibraryDefinition.RootPath)));
        Assert.Null(typeof(DocumentProvenance).GetProperty(nameof(DirectoryLibraryDefinition.RootPath)));
    }
}
