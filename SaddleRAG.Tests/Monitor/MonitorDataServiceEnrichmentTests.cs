// MonitorDataServiceEnrichmentTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Models;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class MonitorDataServiceEnrichmentTests
{
    [Fact]
    public async Task GetLibrarySummariesAsyncSortsAlphabeticallyCaseInsensitive()
    {
        var repo = new FakeLibraryRepository();
        repo.AddLibrary(new LibraryRecord
                            {
                                Id = "zeta",
                                Name = "zeta",
                                Hint = string.Empty,
                                CurrentVersion = "1",
                                AllVersions = new List<string> { "1" }
                            });
        repo.AddLibrary(new LibraryRecord
                            {
                                Id = "Alpha",
                                Name = "Alpha",
                                Hint = string.Empty,
                                CurrentVersion = "1",
                                AllVersions = new List<string> { "1" }
                            });
        repo.AddLibrary(new LibraryRecord
                            {
                                Id = "mongodb.driver",
                                Name = "mongodb.driver",
                                Hint = string.Empty,
                                CurrentVersion = "1",
                                AllVersions = new List<string> { "1" }
                            });

        var svc = new MonitorDataService(repo);
        var summaries = await svc.GetLibrarySummariesAsync(TestContext.Current.CancellationToken);

        var ids = summaries.Select(s => s.LibraryId).ToList();
        Assert.Equal(new[] { "Alpha", "mongodb.driver", "zeta" }, ids);
    }

    [Fact]
    public async Task GetLibraryDetailAsyncCarriesSuspectReasonsAndScrapedAt()
    {
        var scraped = new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc);
        var libRepo = new FakeLibraryRepository();
        libRepo.AddLibrary(new LibraryRecord
                               {
                                   Id = "alpha",
                                   Name = "alpha",
                                   Hint = "hello",
                                   CurrentVersion = "1",
                                   AllVersions = new List<string> { "1" }
                               });
        libRepo.AddVersion(new LibraryVersionRecord
                               {
                                   Id = "alpha/1",
                                   LibraryId = "alpha",
                                   Version = "1",
                                   ScrapedAt = scraped,
                                   PageCount = 10,
                                   ChunkCount = 100,
                                   EmbeddingProviderId = "ollama",
                                   EmbeddingModelName = "nomic-embed-text",
                                   EmbeddingDimensions = 768,
                                   BoundaryIssuePct = 2.5,
                                   Suspect = true,
                                   SuspectReasons = new[] { "low confidence", "thin docs" }
                               });

        var svc = new MonitorDataService(libRepo);
        var detail = await svc.GetLibraryDetailAsync("alpha", TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        Assert.True(detail.IsSuspect);
        Assert.Equal(new[] { "low confidence", "thin docs" }, detail.SuspectReasons);
        Assert.Equal(scraped, detail.LastScrapedAt);
        Assert.Equal(2.5, detail.BoundaryIssuePct);
        Assert.Equal("nomic-embed-text", detail.EmbeddingModelName);
    }
}
