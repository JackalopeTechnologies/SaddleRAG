// JobsUnificationMigration.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Database.Migrations;

/// <summary>
///     One-time, idempotent migration that folds the four legacy job
///     collections (<c>scrapeJobs</c>, <c>rescrubJobs</c>,
///     <c>reembedJobs</c>, <c>backgroundJobs</c>) into the unified
///     <c>jobs</c> collection. Runs on startup; safe to invoke
///     repeatedly because each source is dropped after a successful
///     upsert, so subsequent runs see nothing to migrate.
/// </summary>
public sealed class JobsUnificationMigration
{
    public JobsUnificationMigration(SaddleRagDbContext context, ILogger logger)
    {
        mContext = context;
        mLogger = logger;
    }

    private readonly SaddleRagDbContext mContext;
    private readonly ILogger mLogger;

    /// <summary>
    ///     Executes the migration: per legacy collection still present,
    ///     project every document into <see cref="JobRecord" />, upsert
    ///     into <c>jobs</c>, then drop the source.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        IMongoDatabase database = mContext.Jobs.Database;
        IReadOnlyList<string> existing = await ListCollectionNamesAsync(database, ct);

        long total = 0;
        foreach (var pair in smProjectors.Where(p => existing.Contains(p.LegacyName)))
            total += await MigrateOneAsync(database, pair.LegacyName, pair.Projector, ct);

        if (total > 0)
            mLogger.LogInformation(MigrationCompleteFormat, total);
    }

    private async Task<long> MigrateOneAsync(IMongoDatabase database,
                                              string legacyName,
                                              Func<BsonDocument, JobRecord> projector,
                                              CancellationToken ct)
    {
        long migrated = 0;
        try
        {
            IMongoCollection<BsonDocument> source = database.GetCollection<BsonDocument>(legacyName);
            IReadOnlyList<BsonDocument> docs = await source.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(ct);
            IReadOnlyList<JobRecord> projected = ProjectSafely(docs, projector, legacyName);
            await UpsertAllAsync(projected, ct);
            migrated = projected.Count;
            await database.DropCollectionAsync(legacyName, ct);
            mLogger.LogInformation(LegacyDroppedFormat, migrated, legacyName);
        }
        catch (Exception ex)
        {
            mLogger.LogError(ex, LegacyFailedFormat, legacyName, migrated);
            throw;
        }
        return migrated;
    }

    private IReadOnlyList<JobRecord> ProjectSafely(IEnumerable<BsonDocument> docs,
                                                    Func<BsonDocument, JobRecord> projector,
                                                    string legacyName)
    {
        var result = new List<JobRecord>();
        foreach (BsonDocument doc in docs)
        {
            JobRecord? projected = TryProject(doc, projector, legacyName);
            if (projected != null)
                result.Add(projected);
        }
        return result;
    }

    private JobRecord? TryProject(BsonDocument doc, Func<BsonDocument, JobRecord> projector, string legacyName)
    {
        JobRecord? result = null;
        try
        {
            result = projector(doc);
        }
        catch (Exception ex)
        {
            string id = doc.GetValueOrString(IdField, UnknownIdLabel);
            mLogger.LogWarning(ex, ProjectorFailedFormat, legacyName, id);
        }
        return result;
    }

    private async Task UpsertAllAsync(IReadOnlyList<JobRecord> records, CancellationToken ct)
    {
        foreach (JobRecord record in records)
        {
            await mContext.Jobs.ReplaceOneAsync(j => j.Id == record.Id,
                                                record,
                                                new ReplaceOptions { IsUpsert = true },
                                                ct
                                               );
        }
    }

    private static async Task<IReadOnlyList<string>> ListCollectionNamesAsync(IMongoDatabase database, CancellationToken ct)
    {
        using IAsyncCursor<string> cursor = await database.ListCollectionNamesAsync(cancellationToken: ct);
        return await cursor.ToListAsync(ct);
    }

    private static JobRecord ProjectScrape(BsonDocument doc)
    {
        BsonDocument? job = doc.TryGetValue(LegacyJobField, out BsonValue jv) && jv is BsonDocument jd ? jd : null;
        var scrapeProgress = new ScrapeProgress
        {
            PagesQueued     = doc.GetValueOrInt(PagesQueuedField),
            PagesFetched    = doc.GetValueOrInt(PagesFetchedField),
            PagesClassified = doc.GetValueOrInt(PagesClassifiedField),
            ChunksGenerated = doc.GetValueOrInt(ChunksGeneratedField),
            ChunksEmbedded  = doc.GetValueOrInt(ChunksEmbeddedField),
            ChunksCompleted = doc.GetValueOrInt(ChunksCompletedField),
            PagesCompleted  = doc.GetValueOrInt(PagesCompletedField)
        };
        return new JobRecord
        {
            Id              = doc.GetValueOrString(IdField),
            JobType         = JobType.Scrape,
            Profile         = doc.GetValueOrNullableString(ProfileField),
            LibraryId       = job?.GetValueOrNullableString(LegacyLibraryIdField),
            Version         = job?.GetValueOrNullableString(LegacyVersionField),
            InputJson       = job?.ToJson(),
            Status          = doc.GetValueOrEnum(StatusField, JobStatus.Queued),
            PipelineState   = doc.GetValueOrString(PipelineStateField, nameof(JobStatus.Queued)),
            ItemsProcessed  = scrapeProgress.PagesCompleted,
            ItemsTotal      = 0,
            ItemsLabel      = ItemsLabelPages,
            ScrapeProgress  = scrapeProgress,
            ErrorCount      = doc.GetValueOrInt(ErrorCountField),
            ErrorMessage    = doc.GetValueOrNullableString(ErrorMessageField),
            CreatedAt       = doc.GetValueOrDate(CreatedAtField, DateTime.UtcNow),
            StartedAt       = doc.GetValueOrNullableDate(StartedAtField),
            CompletedAt     = doc.GetValueOrNullableDate(CompletedAtField),
            LastProgressAt  = doc.GetValueOrNullableDate(LastProgressAtField),
            CancelledAt     = doc.GetValueOrNullableDate(CancelledAtField)
        };
    }

    private static JobRecord ProjectRescrub(BsonDocument doc) =>
        ProjectChunkScoped(doc, JobType.Rescrub);

    private static JobRecord ProjectReembed(BsonDocument doc) =>
        ProjectChunkScoped(doc, JobType.Reembed);

    private static JobRecord ProjectChunkScoped(BsonDocument doc, JobType jobType) => new()
    {
        Id              = doc.GetValueOrString(IdField),
        JobType         = jobType,
        Profile         = doc.GetValueOrNullableString(ProfileField),
        LibraryId       = doc.GetValueOrNullableString(LegacyLibraryIdField),
        Version         = doc.GetValueOrNullableString(LegacyVersionField),
        InputJson       = doc.TryGetValue(OptionsField, out BsonValue opt) ? opt.ToJson() : null,
        Status          = doc.GetValueOrEnum(StatusField, JobStatus.Queued),
        PipelineState   = doc.GetValueOrString(PipelineStateField, nameof(JobStatus.Queued)),
        ItemsProcessed  = doc.GetValueOrInt(ChunksProcessedField),
        ItemsTotal      = doc.GetValueOrInt(ChunksTotalField),
        ItemsLabel      = ItemsLabelChunks,
        ResultJson      = doc.TryGetValue(ResultField, out BsonValue res) ? res.ToJson() : null,
        ErrorMessage    = doc.GetValueOrNullableString(ErrorMessageField),
        CreatedAt       = doc.GetValueOrDate(CreatedAtField, DateTime.UtcNow),
        StartedAt       = doc.GetValueOrNullableDate(StartedAtField),
        CompletedAt     = doc.GetValueOrNullableDate(CompletedAtField),
        LastProgressAt  = doc.GetValueOrNullableDate(LastProgressAtField),
        CancelledAt     = doc.GetValueOrNullableDate(CancelledAtField)
    };

    private static JobRecord ProjectBackground(BsonDocument doc) => new()
    {
        Id              = doc.GetValueOrString(IdField),
        JobType         = LegacyBackgroundTypeToEnum(doc.GetValueOrString(LegacyJobTypeField)),
        Profile         = doc.GetValueOrNullableString(ProfileField),
        LibraryId       = doc.GetValueOrNullableString(LegacyLibraryIdField),
        Version         = doc.GetValueOrNullableString(LegacyVersionField),
        InputJson       = doc.GetValueOrNullableString(LegacyInputJsonField),
        Status          = doc.GetValueOrEnum(StatusField, JobStatus.Queued),
        PipelineState   = doc.GetValueOrString(PipelineStateField, nameof(JobStatus.Queued)),
        ItemsProcessed  = doc.GetValueOrInt(LegacyItemsProcessedField),
        ItemsTotal      = doc.GetValueOrInt(LegacyItemsTotalField),
        ItemsLabel      = doc.GetValueOrNullableString(LegacyItemsLabelField),
        ResultJson      = doc.GetValueOrNullableString(LegacyResultJsonField),
        ErrorMessage    = doc.GetValueOrNullableString(ErrorMessageField),
        CreatedAt       = doc.GetValueOrDate(CreatedAtField, DateTime.UtcNow),
        StartedAt       = doc.GetValueOrNullableDate(StartedAtField),
        CompletedAt     = doc.GetValueOrNullableDate(CompletedAtField),
        LastProgressAt  = doc.GetValueOrNullableDate(LastProgressAtField),
        CancelledAt     = doc.GetValueOrNullableDate(CancelledAtField)
    };

    private static JobType LegacyBackgroundTypeToEnum(string legacyType) => legacyType switch
    {
        "dryrun_scrape"              => JobType.DryRunScrape,
        "rechunk"                    => JobType.Rechunk,
        "rename_library"             => JobType.RenameLibrary,
        "delete_version"             => JobType.DeleteVersion,
        "delete_library"             => JobType.DeleteLibrary,
        "index_project_dependencies" => JobType.IndexProjectDependencies,
        "submit_url_correction"      => JobType.SubmitUrlCorrection,
        "cleanup_audit_log"          => JobType.CleanupAuditLog,
        "cleanup_jobs"               => JobType.CleanupJobs,
        "cleanup_orphans"            => JobType.CleanupOrphans,
        var _                        => JobType.Unknown
    };

    private static readonly (string LegacyName, Func<BsonDocument, JobRecord> Projector)[] smProjectors =
    [
        (LegacyCollectionScrapeJobs,     ProjectScrape),
        (LegacyCollectionRescrubJobs,    ProjectRescrub),
        (LegacyCollectionReembedJobs,    ProjectReembed),
        (LegacyCollectionBackgroundJobs, ProjectBackground)
    ];

    private const string LegacyCollectionScrapeJobs = "scrapeJobs";
    private const string LegacyCollectionRescrubJobs = "rescrubJobs";
    private const string LegacyCollectionReembedJobs = "reembedJobs";
    private const string LegacyCollectionBackgroundJobs = "backgroundJobs";

    private const string IdField = "_id";
    private const string ProfileField = "Profile";
    private const string StatusField = "Status";
    private const string PipelineStateField = "PipelineState";
    private const string ErrorMessageField = "ErrorMessage";
    private const string ErrorCountField = "ErrorCount";
    private const string CreatedAtField = "CreatedAt";
    private const string StartedAtField = "StartedAt";
    private const string CompletedAtField = "CompletedAt";
    private const string LastProgressAtField = "LastProgressAt";
    private const string CancelledAtField = "CancelledAt";
    private const string OptionsField = "Options";
    private const string ResultField = "Result";
    private const string LegacyJobField = "Job";
    private const string LegacyLibraryIdField = "LibraryId";
    private const string LegacyVersionField = "Version";
    private const string LegacyJobTypeField = "JobType";
    private const string LegacyInputJsonField = "InputJson";
    private const string LegacyResultJsonField = "ResultJson";
    private const string LegacyItemsProcessedField = "ItemsProcessed";
    private const string LegacyItemsTotalField = "ItemsTotal";
    private const string LegacyItemsLabelField = "ItemsLabel";
    private const string PagesQueuedField = "PagesQueued";
    private const string PagesFetchedField = "PagesFetched";
    private const string PagesClassifiedField = "PagesClassified";
    private const string ChunksGeneratedField = "ChunksGenerated";
    private const string ChunksEmbeddedField = "ChunksEmbedded";
    private const string ChunksCompletedField = "ChunksCompleted";
    private const string PagesCompletedField = "PagesCompleted";
    private const string ChunksProcessedField = "ChunksProcessed";
    private const string ChunksTotalField = "ChunksTotal";
    private const string ItemsLabelPages = "pages";
    private const string ItemsLabelChunks = "chunks";
    private const string UnknownIdLabel = "(no _id)";
    private const string MigrationCompleteFormat =
        "JobsUnificationMigration: migrated {Count} legacy job records into the unified jobs collection.";
    private const string LegacyDroppedFormat =
        "JobsUnificationMigration: migrated {Count} records from {LegacyName} and dropped the source collection.";
    private const string LegacyFailedFormat =
        "JobsUnificationMigration: failed while migrating {LegacyName} after {Migrated} record(s); source collection left in place for retry.";
    private const string ProjectorFailedFormat =
        "JobsUnificationMigration: projector for {LegacyName} threw on document {Id}; skipping.";
}
