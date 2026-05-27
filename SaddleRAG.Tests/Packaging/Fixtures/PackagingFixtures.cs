// PackagingFixtures.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Collections.Generic;
using System.Linq;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Packaging.Fixtures;

internal static class PackagingFixtures
{
    public const int DefaultDim = 8;

    public static LibraryRecord MakeLibrary(string id = "test-lib",
                                            string currentVersion = "1.0",
                                            params string[] allVersions)
    {
        var versions = allVersions.Length > 0 ? allVersions.ToList() : new List<string> { currentVersion };
        return new LibraryRecord
                   {
                       Id = id,
                       Name = id,
                       Hint = "fixture",
                       CurrentVersion = currentVersion,
                       AllVersions = versions
                   };
    }

    public static LibraryVersionRecord MakeVersion(string libraryId = "test-lib",
                                                   string version = "1.0",
                                                   int pageCount = 2,
                                                   int chunkCount = 3,
                                                   int dim = DefaultDim,
                                                   string modelName = "test-embed")
    {
        return new LibraryVersionRecord
                   {
                       Id = $"{libraryId}-{version}",
                       LibraryId = libraryId,
                       Version = version,
                       ScrapedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                       PageCount = pageCount,
                       ChunkCount = chunkCount,
                       EmbeddingProviderId = "onnx-local",
                       EmbeddingModelName = modelName,
                       EmbeddingDimensions = dim
                   };
    }

    public static IReadOnlyList<PageRecord> MakePages(string libraryId = "test-lib",
                                                      string version = "1.0",
                                                      int count = 2)
    {
        var result = new List<PageRecord>();
        for (int i = 0; i < count; i++)
            result.Add(new PageRecord
                           {
                               Id = $"{libraryId}-{version}-p{i}",
                               LibraryId = libraryId,
                               Version = version,
                               Url = $"https://example.test/p{i}",
                               Title = $"Page {i}",
                               Category = DocCategory.HowTo,
                               RawContent = $"Body of page {i}",
                               FetchedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                               ContentHash = $"hash-p{i}",
                               Depth = 0
                           });
        return result;
    }

    public static IReadOnlyList<DocChunk> MakeChunks(string libraryId = "test-lib",
                                                     string version = "1.0",
                                                     int count = 3,
                                                     int dim = DefaultDim)
    {
        var result = new List<DocChunk>();
        for (int i = 0; i < count; i++)
            result.Add(new DocChunk
                           {
                               Id = $"{libraryId}-{version}-c{i}",
                               LibraryId = libraryId,
                               Version = version,
                               PageUrl = $"https://example.test/p{i % 2}",
                               PageTitle = $"Page {i % 2}",
                               Category = DocCategory.HowTo,
                               Content = $"Chunk {i} text body",
                               TokenCount = 12,
                               Embedding = Enumerable.Range(0, dim).Select(j => (float)(i * dim + j) / 100f).ToArray()
                           });
        return result;
    }
}
