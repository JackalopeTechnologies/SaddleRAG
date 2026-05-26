// ServiceCollectionExtensions.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson.Serialization;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Database;

/// <summary>
///     Registers SaddleRAG MongoDB services into the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Adds SaddleRAG MongoDB database services with profile support.
    /// </summary>
    public static IServiceCollection AddSaddleRagDatabase(this IServiceCollection services,
                                                          IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        RegisterClassMaps();

        services.Configure<SaddleRagDbSettings>(configuration.GetSection(SaddleRagDbSettings.SectionName));

        // Factory enables per-profile context creation (multi-user MCP support)
        services.AddSingleton<SaddleRagDbContextFactory>();
        services.AddSingleton<RepositoryFactory>();

        // Default-profile singletons (used by ingestion and the default MCP path)
        services.AddSingleton<SaddleRagDbContext>(sp =>
                                                      sp.GetRequiredService<SaddleRagDbContextFactory>().GetDefault()
                                                 );
        services.AddSingleton<ILibraryRepository, LibraryRepository>();
        services.AddSingleton<IPageRepository, PageRepository>();
        services.AddSingleton<IChunkRepository, ChunkRepository>();
        services.AddSingleton<IDiffRepository, DiffRepository>();
        services.AddSingleton<IScrapeAuditRepository, ScrapeAuditRepository>();
        services.AddSingleton<ILibraryProfileRepository, LibraryProfileRepository>();
        services.AddSingleton<ILibraryIndexRepository, LibraryIndexRepository>();
        services.AddSingleton<IBm25ShardRepository, Bm25ShardRepository>();
        services.AddSingleton<IExcludedSymbolsRepository, ExcludedSymbolsRepository>();
        services.AddSingleton<IJobRepository, JobRepository>();

        return services;
    }

    private static void RegisterClassMaps()
    {
        // Bm25Stats replaced the older Bm25Index; existing documents may
        // still have the old "Postings" field at the Bm25 level. Tolerate
        // it on read so the next rescrub can repopulate cleanly.
        if (!BsonClassMap.IsClassMapRegistered(typeof(Bm25Stats)))
        {
            BsonClassMap.RegisterClassMap<Bm25Stats>(cm =>
                                                     {
                                                         cm.AutoMap();
                                                         cm.SetIgnoreExtraElements(ignoreExtraElements: true);
                                                     }
                                                    );
        }

        if (!BsonClassMap.IsClassMapRegistered(typeof(LibraryIndex)))
        {
            BsonClassMap.RegisterClassMap<LibraryIndex>(cm =>
                                                        {
                                                            cm.AutoMap();
                                                            cm.SetIgnoreExtraElements(ignoreExtraElements: true);
                                                        }
                                                       );
        }
    }
}
