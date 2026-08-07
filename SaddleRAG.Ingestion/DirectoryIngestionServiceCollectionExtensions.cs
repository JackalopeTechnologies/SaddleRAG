// DirectoryIngestionServiceCollectionExtensions.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Ingestion.Chunking;
using SaddleRAG.Ingestion.Crawling;
using SaddleRAG.Ingestion.Documents.Intake;
using SaddleRAG.Ingestion.Scanning;
using SaddleRAG.Ingestion.Services;
using SaddleRAG.Ingestion.Subjects;
using SaddleRAG.Ingestion.Symbols;

namespace SaddleRAG.Ingestion;

/// <summary>Registers shared document intake plus directory and web-document ingestion services.</summary>
public static class DirectoryIngestionServiceCollectionExtensions
{
    public static IServiceCollection AddSaddleRagDirectoryIngestion(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IDirectoryScanFileSystem, PhysicalDirectoryScanFileSystem>();
        services.TryAddSingleton<IDirectoryDocumentCapabilityPreflight,
                                 DirectoryDocumentCapabilityPreflight>();
        services.TryAddSingleton<DirectoryRootValidator>();
        services.TryAddSingleton<IDocumentIntake, DocumentIntakeService>();
        services.TryAddSingleton<WebDocumentPageProducer>();
        services.TryAddSingleton<SymbolExtractor>();
        services.TryAddSingleton<CategoryAwareChunker>();
        services.TryAddSingleton<SubjectDescriptorBuilder>();
        services.TryAddSingleton<SubjectCatalogBuilder>();
        services.TryAddSingleton<SubjectClassifier>();
        services.TryAddSingleton<ISubjectClassifier>(provider =>
            provider.GetRequiredService<SubjectClassifier>());
        services.TryAddSingleton<DirectoryScanEngine>();
        services.TryAddSingleton<DirectoryPageProducer>();
        services.TryAddSingleton<IngestionPageProcessor>();
        services.TryAddSingleton<DirectoryIngestionPipeline>();
        services.TryAddSingleton<IDirectoryIngestionPipeline>(provider =>
            provider.GetRequiredService<DirectoryIngestionPipeline>());
        services.TryAddSingleton<DirectoryLibraryRegistrationService>();
        services.TryAddSingleton<IDirectoryLibraryRegistrationService>(provider =>
            provider.GetRequiredService<DirectoryLibraryRegistrationService>());
        services.TryAddSingleton<ILibraryDeletionService, LibraryDeletionService>();
        services.TryAddSingleton<ILibraryRenameService, LibraryRenameService>();
        services.TryAddSingleton<DirectoryIngestionCoordinator>();
        services.TryAddSingleton<IDirectoryIngestionCoordinator>(provider =>
            provider.GetRequiredService<DirectoryIngestionCoordinator>());
        services.TryAddSingleton<DirectoryScanVersionProvider>();
        services.TryAddSingleton<DirectoryScanJobRunner>();
        services.TryAddSingleton<IDirectoryScanJobQueue>(provider =>
            provider.GetRequiredService<DirectoryScanJobRunner>());
        return services;
    }
}
