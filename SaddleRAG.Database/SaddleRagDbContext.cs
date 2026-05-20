// SaddleRagDbContext.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Database;

/// <summary>
///     Provides typed access to MongoDB collections for the SaddleRAG system.
/// </summary>
public class SaddleRagDbContext
{
    static SaddleRagDbContext()
    {
        // JobRecord persists JobType and JobStatus as MongoDB strings
        // rather than ints so documents are human-readable and immune
        // to enum-reorder drift. Core has no MongoDB dependency, so
        // the serializer configuration lives here in the database
        // layer via a class-map registration. The IsClassMapRegistered
        // guard prevents a throw if tests construct DbContext multiple
        // times within the same AppDomain.
        if (!BsonClassMap.IsClassMapRegistered(typeof(JobRecord)))
        {
            BsonClassMap.RegisterClassMap<JobRecord>(cm =>
                                                     {
                                                         cm.AutoMap();
                                                         cm.MapMember(r => r.JobType)
                                                           .SetSerializer(new EnumSerializer<JobType>(BsonType.String));
                                                         cm.MapMember(r => r.Status)
                                                           .SetSerializer(new EnumSerializer<JobStatus>(BsonType.String));
                                                     }
                                                    );
        }
    }

    public SaddleRagDbContext(IOptions<SaddleRagDbSettings> settings)
    {
        (var connectionString, var databaseName) = settings.Value.Resolve();
        var client = new MongoClient(connectionString);
        mDatabase = client.GetDatabase(databaseName);
    }

    public IMongoCollection<LibraryRecord> Libraries =>
        mDatabase.GetCollection<LibraryRecord>(CollectionLibraries);

    public IMongoCollection<LibraryVersionRecord> LibraryVersions =>
        mDatabase.GetCollection<LibraryVersionRecord>(CollectionLibraryVersions);

    public IMongoCollection<PageRecord> Pages =>
        mDatabase.GetCollection<PageRecord>(CollectionPages);

    public IMongoCollection<DocChunk> Chunks =>
        mDatabase.GetCollection<DocChunk>(CollectionChunks);

    public IMongoCollection<VersionDiffRecord> VersionDiffs =>
        mDatabase.GetCollection<VersionDiffRecord>(CollectionVersionDiffs);

    public IMongoCollection<ProjectProfile> ProjectProfiles =>
        mDatabase.GetCollection<ProjectProfile>(CollectionProjectProfiles);

    /// <summary>
    ///     Unified jobs collection. Replaced the four legacy per-pipeline
    ///     collections (scrapeJobs / rescrubJobs / reembedJobs /
    ///     backgroundJobs). Any pre-unification data is migrated into this
    ///     collection on startup by <c>JobsUnificationMigration</c>; the
    ///     legacy collections are dropped once migration completes.
    /// </summary>
    public IMongoCollection<JobRecord> Jobs =>
        mDatabase.GetCollection<JobRecord>(CollectionJobs);

    public IMongoCollection<LibraryProfile> LibraryProfiles =>
        mDatabase.GetCollection<LibraryProfile>(CollectionLibraryProfiles);

    public IMongoCollection<LibraryIndex> LibraryIndexes =>
        mDatabase.GetCollection<LibraryIndex>(CollectionLibraryIndexes);

    public IMongoCollection<Bm25Shard> Bm25Shards =>
        mDatabase.GetCollection<Bm25Shard>(CollectionBm25Shards);

    public IMongoCollection<ExcludedSymbol> ExcludedSymbols =>
        mDatabase.GetCollection<ExcludedSymbol>(CollectionExcludedSymbols);

    public IMongoCollection<ScrapeAuditLogEntry> ScrapeAuditLog =>
        mDatabase.GetCollection<ScrapeAuditLogEntry>(CollectionScrapeAuditLog);

    /// <summary>
    ///     GridFS bucket for spilled BM25 payloads (per-term postings or
    ///     entire shards) that exceed the inline 16MB Mongo document
    ///     limit. Reader and writer talk to this bucket via the shard
    ///     repository — callers don't construct it directly.
    /// </summary>
    public IGridFSBucket Bm25Bucket =>
        new GridFSBucket(mDatabase, new GridFSBucketOptions { BucketName = Bm25BucketName });

    /// <summary>
    ///     Underlying <see cref="IMongoDatabase" />. Exposed for maintenance
    ///     operations (e.g. <c>compact_collections</c>) that need to issue
    ///     admin commands or enumerate collections — repository getters
    ///     remain the right entry point for typed CRUD.
    /// </summary>
    public IMongoDatabase Database => mDatabase;

    private readonly IMongoDatabase mDatabase;

    /// <summary>
    ///     Ensures required indexes exist on all collections.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        // Pages: compound unique index on LibraryId + Version + Url
        var pageKeys = Builders<PageRecord>.IndexKeys;
        await
            Pages.Indexes.CreateOneAsync(new
                                             CreateIndexModel<PageRecord>(pageKeys.Combine(pageKeys.Ascending(p => p
                                                                                           .LibraryId
                                                                                       ),
                                                                                   pageKeys.Ascending(p => p.Version),
                                                                                   pageKeys.Ascending(p => p.Url)
                                                                              ),
                                                                          new CreateIndexOptions { Unique = true }
                                                                         ),
                                         cancellationToken: ct
                                        );

        // Chunks: compound index on LibraryId + Version + Category
        var chunkKeys = Builders<DocChunk>.IndexKeys;
        await
            Chunks.Indexes.CreateOneAsync(new
                                              CreateIndexModel<DocChunk>(chunkKeys.Combine(chunkKeys.Ascending(c => c
                                                                                          .LibraryId
                                                                                      ),
                                                                                  chunkKeys.Ascending(c => c.Version),
                                                                                  chunkKeys.Ascending(c => c.Category)
                                                                             )
                                                                        ),
                                          cancellationToken: ct
                                         );

        // Chunks: sparse index on QualifiedName for API reference lookups
        await Chunks.Indexes.CreateOneAsync(new CreateIndexModel<DocChunk>(chunkKeys.Ascending(c => c.QualifiedName),
                                                                           new CreateIndexOptions { Sparse = true }
                                                                          ),
                                            cancellationToken: ct
                                           );

        // Chunks: compound index on LibraryId + Version + ParserVersion for STALE detection
        await
            Chunks.Indexes.CreateOneAsync(new
                                              CreateIndexModel<DocChunk>(chunkKeys.Combine(chunkKeys.Ascending(c => c
                                                                                          .LibraryId
                                                                                      ),
                                                                                  chunkKeys.Ascending(c => c.Version),
                                                                                  chunkKeys.Ascending(c => c
                                                                                          .ParserVersion
                                                                                      )
                                                                             )
                                                                        ),
                                          cancellationToken: ct
                                         );

        // LibraryProfiles: compound index on LibraryId + Version
        var profileKeys = Builders<LibraryProfile>.IndexKeys;
        await
            LibraryProfiles.Indexes.CreateOneAsync(new CreateIndexModel<LibraryProfile>(profileKeys.Combine(profileKeys
                                                                        .Ascending(p => p.LibraryId),
                                                                    profileKeys.Ascending(p => p.Version)
                                                               )
                                                       ),
                                                   cancellationToken: ct
                                                  );

        // LibraryIndexes: compound index on LibraryId + Version
        var indexKeys = Builders<LibraryIndex>.IndexKeys;
        await
            LibraryIndexes.Indexes.CreateOneAsync(new CreateIndexModel<LibraryIndex>(indexKeys.Combine(indexKeys
                                                                       .Ascending(i => i.LibraryId),
                                                                   indexKeys.Ascending(i => i.Version)
                                                              )
                                                      ),
                                                  cancellationToken: ct
                                                 );

        // Bm25Shards: compound index on LibraryId + Version + ShardIndex
        // for batch-load by (lib, ver) and pinpoint lookup by shard.
        var shardKeys = Builders<Bm25Shard>.IndexKeys;
        await
            Bm25Shards.Indexes.CreateOneAsync(new
                                                  CreateIndexModel<Bm25Shard>(shardKeys.Combine(shardKeys.Ascending(s =>
                                                                                               s.LibraryId
                                                                                           ),
                                                                                       shardKeys
                                                                                           .Ascending(s => s.Version),
                                                                                       shardKeys
                                                                                           .Ascending(s => s.ShardIndex)
                                                                                  )
                                                                             ),
                                              cancellationToken: ct
                                             );

        // ExcludedSymbols: compound on (LibraryId, Version, Reason) for the
        // list_excluded_symbols reason filter, plus (LibraryId, Version, Name)
        // for fast remove-by-name when the LLM promotes/demotes tokens.
        var excludedKeys = Builders<ExcludedSymbol>.IndexKeys;
        await
            ExcludedSymbols.Indexes.CreateOneAsync(new
                                                       CreateIndexModel<ExcludedSymbol>(excludedKeys
                                                               .Combine(excludedKeys.Ascending(e => e.LibraryId),
                                                                        excludedKeys.Ascending(e => e.Version),
                                                                        excludedKeys.Ascending(e => e.Reason)
                                                                       )
                                                           ),
                                                   cancellationToken: ct
                                                  );
        await
            ExcludedSymbols.Indexes.CreateOneAsync(new
                                                       CreateIndexModel<ExcludedSymbol>(excludedKeys
                                                               .Combine(excludedKeys.Ascending(e => e.LibraryId),
                                                                        excludedKeys.Ascending(e => e.Version),
                                                                        excludedKeys.Ascending(e => e.Name)
                                                                       )
                                                           ),
                                                   cancellationToken: ct
                                                  );

        // ScrapeAuditLog: bucketed query by status/skip-reason
        var auditKeys = Builders<ScrapeAuditLogEntry>.IndexKeys;
        await
            ScrapeAuditLog.Indexes.CreateOneAsync(new CreateIndexModel<ScrapeAuditLogEntry>(auditKeys.Combine(auditKeys
                                                                       .Ascending(a => a.JobId),
                                                                   auditKeys.Ascending(a => a.Status),
                                                                   auditKeys.Ascending(a => a.SkipReason)
                                                              )
                                                      ),
                                                  cancellationToken: ct
                                                 );

        // ScrapeAuditLog: by-host views
        await
            ScrapeAuditLog.Indexes.CreateOneAsync(new CreateIndexModel<ScrapeAuditLogEntry>(auditKeys.Combine(auditKeys
                                                                       .Ascending(a => a.JobId),
                                                                   auditKeys.Ascending(a => a.Host)
                                                              )
                                                      ),
                                                  cancellationToken: ct
                                                 );

        // ScrapeAuditLog: single-URL forensics
        await
            ScrapeAuditLog.Indexes.CreateOneAsync(new CreateIndexModel<ScrapeAuditLogEntry>(auditKeys.Combine(auditKeys
                                                                       .Ascending(a => a.JobId),
                                                                   auditKeys.Ascending(a => a.Url)
                                                              )
                                                      ),
                                                  cancellationToken: ct
                                                 );

        // ScrapeAuditLog: TTL on DiscoveredAt. Auto-purges audit rows
        // older than smJobRetention. Manual cleanup_audit_log tool covers
        // early eviction.
        await
            ScrapeAuditLog.Indexes.CreateOneAsync(new CreateIndexModel<ScrapeAuditLogEntry>(auditKeys.Ascending(a => a
                                                                   .DiscoveredAt
                                                               ),
                                                           new CreateIndexOptions
                                                               {
                                                                   ExpireAfter = smJobRetention
                                                               }
                                                      ),
                                                  cancellationToken: ct
                                                 );

        // Jobs (unified): TTL on CompletedAt. Running jobs
        // (CompletedAt = null) are skipped by Mongo's TTL purge; only
        // terminal jobs eventually age out. Manual cleanup_jobs tool
        // covers early eviction.
        var jobKeys = Builders<JobRecord>.IndexKeys;
        await Jobs.Indexes.CreateOneAsync(new CreateIndexModel<JobRecord>(jobKeys.Ascending(j => j.CompletedAt),
                                                                          new CreateIndexOptions
                                                                              {
                                                                                  ExpireAfter = smJobRetention
                                                                              }
                                                                         ),
                                          cancellationToken: ct
                                         );
    }

    private const int JobRetentionDays = 30;

    private const string CollectionLibraries = "libraries";
    private const string CollectionLibraryVersions = "libraryVersions";
    private const string CollectionPages = "pages";
    private const string CollectionChunks = "chunks";
    private const string CollectionVersionDiffs = "versionDiffs";
    private const string CollectionProjectProfiles = "projectProfiles";
    internal const string CollectionJobs = "jobs";
    private const string CollectionLibraryProfiles = "libraryProfiles";
    private const string CollectionLibraryIndexes = "libraryIndexes";
    private const string CollectionBm25Shards = "bm25Shards";
    private const string CollectionExcludedSymbols = "library_excluded_symbols";
    private const string CollectionScrapeAuditLog = "scrape_audit_log";
    private const string Bm25BucketName = "bm25";

    private static readonly TimeSpan smJobRetention = TimeSpan.FromDays(JobRetentionDays);
}
