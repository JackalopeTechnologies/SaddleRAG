// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class PublishedVersionPageToolsTests
{
    [Theory]
    [InlineData(VersionPublicationState.Building)]
    [InlineData(VersionPublicationState.Failed)]
    public async Task ListPagesDoesNotReadPagesForUnpublishedVersion(VersionPublicationState state)
    {
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var libraryRepository = Substitute.For<ILibraryRepository>();
        var pageRepository = Substitute.For<IPageRepository>();
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepository);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepository);
        libraryRepository.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                         .Returns(new LibraryRecord
                                      {
                                          Id = "foo",
                                          Name = "f",
                                          Hint = "h",
                                          CurrentVersion = "published",
                                          AllVersions = ["published"]
                                      }
                                 );
        libraryRepository.GetVersionAsync("foo", "candidate", Arg.Any<CancellationToken>())
                         .Returns(new LibraryVersionRecord
                                      {
                                          Id = "foo/candidate",
                                           LibraryId = "foo",
                                           Version = "candidate",
                                           ScrapedAt = DateTime.UtcNow,
                                           PageCount = 0,
                                           ChunkCount = 0,
                                           EmbeddingProviderId = "ollama",
                                          EmbeddingModelName = "nomic-embed-text",
                                          EmbeddingDimensions = 768,
                                          PublicationState = state
                                      }
                                 );

        var json = await PageTools.ListPages(factory,
                                             "foo",
                                             version: "candidate",
                                             profile: null,
                                             TestContext.Current.CancellationToken
                                            );

        Assert.Contains("not published", json, StringComparison.OrdinalIgnoreCase);
        await pageRepository.DidNotReceiveWithAnyArgs()
                            .GetPagesAsync(default!, default!, TestContext.Current.CancellationToken);
    }
}
