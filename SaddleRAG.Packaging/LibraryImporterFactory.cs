// LibraryImporterFactory.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Packaging;

/// <summary>
///     Creates importers whose repositories are scoped to one database profile.
/// </summary>
public sealed class LibraryImporterFactory
{
    public LibraryImporterFactory(RepositoryFactory repositoryFactory,
                                  IEmbeddingProvider embeddingProvider,
                                  ICollectionCompactor compactor,
                                  ILibraryDeletionService deletionService,
                                  ILibraryIngestionModeLeaseManager modeLeaseManager,
                                  IReembedJobDispatcher reembedJobDispatcher)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(compactor);
        ArgumentNullException.ThrowIfNull(deletionService);
        ArgumentNullException.ThrowIfNull(modeLeaseManager);
        ArgumentNullException.ThrowIfNull(reembedJobDispatcher);
        mRepositoryFactory = repositoryFactory;
        mEmbeddingProvider = embeddingProvider;
        mCompactor = compactor;
        mDeletionService = deletionService;
        mModeLeaseManager = modeLeaseManager;
        mReembedJobDispatcher = reembedJobDispatcher;
    }

    private readonly RepositoryFactory mRepositoryFactory;
    private readonly IEmbeddingProvider mEmbeddingProvider;
    private readonly ICollectionCompactor mCompactor;
    private readonly ILibraryDeletionService mDeletionService;
    private readonly ILibraryIngestionModeLeaseManager mModeLeaseManager;
    private readonly IReembedJobDispatcher mReembedJobDispatcher;

    /// <summary>
    ///     Creates an importer backed entirely by repositories for <paramref name="profile" />.
    /// </summary>
    public LibraryImporter Create(string? profile = null) =>
        new(mRepositoryFactory.GetLibraryRepository(profile),
            mRepositoryFactory.GetJobRepository(profile),
            mEmbeddingProvider,
            mRepositoryFactory.GetLibraryProfileRepository(profile),
            mRepositoryFactory.GetLibraryIndexRepository(profile),
            mRepositoryFactory.GetExcludedSymbolsRepository(profile),
            mRepositoryFactory.GetDiffRepository(profile),
            mRepositoryFactory.GetPageRepository(profile),
            mRepositoryFactory.GetChunkRepository(profile),
            mRepositoryFactory.GetBm25ShardRepository(profile),
            mRepositoryFactory.GetSourceDocumentRepository(profile),
            mRepositoryFactory.GetSubjectCatalogRepository(profile),
            mRepositoryFactory.GetSubjectAssignmentRepository(profile),
            mCompactor,
            _ => mRepositoryFactory.GetDatabase(profile),
            mDeletionService,
            mModeLeaseManager,
            mRepositoryFactory.GetLibraryIngestionModeRepository(profile),
            mReembedJobDispatcher);
}
