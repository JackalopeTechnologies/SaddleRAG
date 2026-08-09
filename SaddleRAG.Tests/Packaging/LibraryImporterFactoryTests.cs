// LibraryImporterFactoryTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Packaging;

#endregion

namespace SaddleRAG.Tests.Packaging;

public sealed class LibraryImporterFactoryTests
{
    [Fact]
    public void CreateRequestsEveryImporterRepositoryForExactProfile()
    {
        const string Profile = "engineering-documents";
        var repositories = Substitute.For<RepositoryFactory>([null!]);
        repositories.GetLibraryRepository(Profile).Returns(Substitute.For<ILibraryRepository>());
        repositories.GetJobRepository(Profile).Returns(Substitute.For<IJobRepository>());
        repositories.GetLibraryProfileRepository(Profile).Returns(Substitute.For<ILibraryProfileRepository>());
        repositories.GetLibraryIndexRepository(Profile).Returns(Substitute.For<ILibraryIndexRepository>());
        repositories.GetExcludedSymbolsRepository(Profile).Returns(Substitute.For<IExcludedSymbolsRepository>());
        repositories.GetDiffRepository(Profile).Returns(Substitute.For<IDiffRepository>());
        repositories.GetPageRepository(Profile).Returns(Substitute.For<IPageRepository>());
        repositories.GetChunkRepository(Profile).Returns(Substitute.For<IChunkRepository>());
        repositories.GetBm25ShardRepository(Profile).Returns(Substitute.For<IBm25ShardRepository>());
        repositories.GetSourceDocumentRepository(Profile).Returns(Substitute.For<ISourceDocumentRepository>());
        repositories.GetSubjectCatalogRepository(Profile).Returns(Substitute.For<ISubjectCatalogRepository>());
        repositories.GetSubjectAssignmentRepository(Profile).Returns(Substitute.For<ISubjectAssignmentRepository>());
        repositories.GetLibraryIngestionModeRepository(Profile)
                    .Returns(Substitute.For<ILibraryIngestionModeRepository>());
        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        var compactor = Substitute.For<ICollectionCompactor>();
        var deletionService = Substitute.For<ILibraryDeletionService>();
        var modeLeaseManager = Substitute.For<ILibraryIngestionModeLeaseManager>();
        var reembedJobDispatcher = Substitute.For<IReembedJobDispatcher>();
        var factory = new LibraryImporterFactory(repositories,
                                                 embeddingProvider,
                                                 compactor,
                                                 deletionService,
                                                 modeLeaseManager,
                                                 reembedJobDispatcher);
        repositories.ClearReceivedCalls();

        LibraryImporter importer = factory.Create(Profile);

        Assert.NotNull(importer);
        Assert.Equal(13, repositories.ReceivedCalls().Count());
        Assert.All(repositories.ReceivedCalls(),
                   call => Assert.Equal(Profile, Assert.Single(call.GetArguments())));
        repositories.Received(1).GetLibraryRepository(Profile);
        repositories.Received(1).GetJobRepository(Profile);
        repositories.Received(1).GetLibraryProfileRepository(Profile);
        repositories.Received(1).GetLibraryIndexRepository(Profile);
        repositories.Received(1).GetExcludedSymbolsRepository(Profile);
        repositories.Received(1).GetDiffRepository(Profile);
        repositories.Received(1).GetPageRepository(Profile);
        repositories.Received(1).GetChunkRepository(Profile);
        repositories.Received(1).GetBm25ShardRepository(Profile);
        repositories.Received(1).GetSourceDocumentRepository(Profile);
        repositories.Received(1).GetSubjectCatalogRepository(Profile);
        repositories.Received(1).GetSubjectAssignmentRepository(Profile);
        repositories.Received(1).GetLibraryIngestionModeRepository(Profile);
    }
}
