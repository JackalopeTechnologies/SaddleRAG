// SearchTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Diagnostics;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Subjects;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools for searching documentation content. Combines vector and
///     keyword rank positions, then optionally combines that ordering with
///     the local reranker's ordering. Independent leaders remain competitive
///     without assuming the providers' score scales are interchangeable.
/// </summary>
[McpServerToolType]
public static class SearchTools
{
    internal record HybridCandidate
    {
        public required DocChunk Chunk { get; init; }
        public required float VectorScore { get; init; }
        public required double Bm25Score { get; init; }
        public double SubjectBoost { get; init; }
        public required double HybridScore { get; init; }
    }

    internal record RankedResult
    {
        public required DocChunk Chunk { get; init; }
        public required float FinalScore { get; init; }
        public required float VectorScore { get; init; }
        public required float Bm25Score { get; init; }
        public float? RerankScore { get; init; }
        public bool SourceIdentityMatch { get; init; }
    }

    [McpServerTool(Name = "search_docs")]
    [McpMeta("anthropic/alwaysLoad", value: true)]
    [Description("Call this FIRST, before answering from memory, for any question that names a " +
                 "library, framework, API, or symbol. " +
                 "Search documentation using natural language. Works across all ingested libraries " +
                 "or filtered to a specific one. Filter by category to narrow results: " +
                 "Overview (concepts, architecture, getting started), " +
                 "HowTo (tutorials, guides, walkthroughs), " +
                 "Sample (code examples, demos), " +
                 "ApiReference (class/method/property docs), " +
                 "ChangeLog (release notes, migration guides). " +
                 "Omit category to search everything."
                )]
    public static async Task<string> SearchDocs(IVectorSearchProvider vectorSearch,
                                                IEmbeddingProvider embeddingProvider,
                                                IReRanker reRanker,
                                                RepositoryFactory repositoryFactory,
                                                IOptions<RankingSettings> rankingOptions,
                                                IQueryMetrics metrics,
                                                ILogger<SearchToolsLog> logger,
                                                [Description("Natural language search query")]
                                                string query,
                                                [Description("Library identifier — omit to search all libraries")]
                                                string? library = null,
                                                [Description("Filter to category: Overview, HowTo, Sample, ApiReference, ChangeLog"
                                                            )]
                                                string? category = null,
                                                [Description("Optional subject id, label, or alias for an explicit library-scoped filter")]
                                                string? subject = null,
                                                [Description("Specific version — defaults to current")]
                                                string? version = null,
                                                [Description("Maximum results (default 5)")]
                                                int maxResults = 5,
                                                [Description("Optional database profile name")]
                                                string? profile = null,
                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vectorSearch);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(reRanker);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(rankingOptions);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(query);

        var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
        var resolvedVersion = await ResolveIfNeeded(libraryRepository, library, version, ct);
        string json;

        if (library != null && resolvedVersion == null)
            json = LibraryNotFoundJson(library);
        else
        {
            try
            {
                SubjectSearchContext subjectContext = await ResolveSubjectContextAsync(repositoryFactory,
                                                                                        libraryRepository,
                                                                                        library,
                                                                                        resolvedVersion,
                                                                                        subject,
                                                                                        query,
                                                                                        profile,
                                                                                        ct);
                json = await metrics.TimeAsync(QueryMetricOperations.SearchDocs,
                                               () => ExecuteSearchAsync(vectorSearch,
                                                                        embeddingProvider,
                                                                        reRanker,
                                                                        repositoryFactory,
                                                                        rankingOptions.Value,
                                                                        metrics,
                                                                        logger,
                                                                        query,
                                                                        library,
                                                                        resolvedVersion,
                                                                        category,
                                                                        subjectContext,
                                                                        maxResults,
                                                                        profile,
                                                                        ct
                                                                       ),
                                               note: $"library={library ?? "*"}"
                                              );
            }
            catch(InvalidDataException ex)
            {
                json = ErrorJson(ex.Message);
            }
        }

        return json;
    }

    /// <summary>
    ///     Logger category for <see cref="SearchTools" />. Separate marker
    ///     type so the static tool class can take <c>ILogger&lt;T&gt;</c>
    ///     via DI on its method parameter list — generic type parameters
    ///     can't point at a static class directly.
    /// </summary>
    public sealed class SearchToolsLog
    {
    }

    [McpServerTool(Name = "get_class_reference")]
    [McpMeta("anthropic/alwaysLoad", value: true)]
    [Description("Call this FIRST, before answering from memory, when a question names a class, " +
                 "type, or symbol. " +
                 "Look up API reference for a specific class or type. " +
                 "If library is omitted, searches across ALL libraries. " +
                 "Tries exact match first, then fuzzy match."
                )]
    public static async Task<string> GetClassReference(RepositoryFactory repositoryFactory,
                                                       [Description("Class name (partial or full)")]
                                                       string className,
                                                       [Description("Library identifier — omit to search all libraries"
                                                                   )]
                                                       string? library = null,
                                                       [Description("Specific version — defaults to current")]
                                                       string? version = null,
                                                       [Description("Optional database profile name")]
                                                       string? profile = null,
                                                       CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(className);

        var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
        var chunkRepository = repositoryFactory.GetChunkRepository(profile);
        var resolvedVersion = await ResolveIfNeeded(libraryRepository, library, version, ct);
        string json;

        if (library != null && resolvedVersion == null)
            json = LibraryNotFoundJson(library);
        else
        {
            var results =
                await FetchClassReferenceAsync(libraryRepository,
                                               chunkRepository,
                                               library,
                                               resolvedVersion,
                                               className,
                                               ct
                                              );
            var response = results.Select(c => new
                                                   {
                                                       c.LibraryId,
                                                       c.QualifiedName,
                                                       c.PageTitle,
                                                       c.SectionPath,
                                                       c.PageUrl,
                                                       c.Content
                                                   }
                                         );
            json = JsonSerializer.Serialize(response, smJsonOptions);
        }

        return json;
    }

    [McpServerTool(Name = "get_library_overview")]
    [McpMeta("anthropic/alwaysLoad", value: true)]
    [Description("Call this FIRST, before answering from memory, when orienting on an unfamiliar " +
                 "library. " +
                 "Get an overview of what a library is and how to get started. " +
                 "Returns Overview-category documentation chunks — actual library content. " +
                 "For diagnostic information (chunk counts, language mix, boundary issues, suspect markers), " +
                 "use get_library_health instead. " +
                 "If no Overview content exists, returns the most relevant chunks of any category."
                )]
    public static async Task<string> GetLibraryOverview(IVectorSearchProvider vectorSearch,
                                                        IEmbeddingProvider embeddingProvider,
                                                        RepositoryFactory repositoryFactory,
                                                        IQueryMetrics metrics,
                                                        [Description("Library identifier")] string library,
                                                        [Description("Specific version — defaults to current")]
                                                        string? version = null,
                                                        [Description("Optional database profile name")]
                                                        string? profile = null,
                                                        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vectorSearch);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
        var resolvedVersion = await LibraryTools.ResolveVersionAsync(libraryRepository, library, version, ct);
        string json;

        if (resolvedVersion == null)
            json = LibraryNotFoundJson(library);
        else
        {
            json = await BuildLibraryOverviewAsync(vectorSearch,
                                                   embeddingProvider,
                                                   metrics,
                                                   library,
                                                   resolvedVersion,
                                                   profile,
                                                   ct
                                                  );
        }

        return json;
    }

    private static async Task<string> BuildLibraryOverviewAsync(IVectorSearchProvider vectorSearch,
                                                                IEmbeddingProvider embeddingProvider,
                                                                IQueryMetrics metrics,
                                                                string library,
                                                                string resolvedVersion,
                                                                string? profile,
                                                                CancellationToken ct)
    {
        var query = $"{library} overview getting started introduction";
        var embeddings = await metrics.TimeAsync(QueryMetricOperations.EmbedQuery,
                                                 () => embeddingProvider.EmbedAsync([query], EmbedRole.Query, ct),
                                                 note: $"library={library}"
                                                );

        var overviewFilter = new VectorSearchFilter
                                 {
                                     Profile = profile,
                                     LibraryId = library,
                                     Version = resolvedVersion,
                                     Category = DocCategory.Overview
                                 };

        var results = await metrics.TimeAsync(QueryMetricOperations.VectorSearch,
                                              () => vectorSearch.SearchAsync(embeddings[0],
                                                                             overviewFilter,
                                                                             MaxOverviewResults,
                                                                             ct
                                                                            ),
                                              r => r.Count,
                                              $"library={library}"
                                             );

        if (results.Count == 0)
        {
            var fallbackFilter = new VectorSearchFilter
                                     {
                                         Profile = profile,
                                         LibraryId = library,
                                         Version = resolvedVersion
                                     };
            results = await metrics.TimeAsync(QueryMetricOperations.VectorSearch,
                                              () => vectorSearch.SearchAsync(embeddings[0],
                                                                             fallbackFilter,
                                                                             MaxOverviewResults,
                                                                             ct
                                                                            ),
                                              r => r.Count,
                                              $"library={library};fallback"
                                             );
        }

        var response = results.Select(r => new
                                               {
                                                   r.Chunk.LibraryId,
                                                   r.Chunk.Category,
                                                   r.Chunk.PageTitle,
                                                   r.Chunk.SectionPath,
                                                   r.Chunk.PageUrl,
                                                   r.Chunk.Content,
                                                   r.Score
                                               }
                                     );

        var json = JsonSerializer.Serialize(response, smJsonOptions);
        return json;
    }

    private static async Task<IReadOnlyList<DocChunk>> FetchClassReferenceAsync(ILibraryRepository libraryRepository,
        IChunkRepository chunkRepository,
        string? library,
        string? resolvedVersion,
        string className,
        CancellationToken ct)
    {
        IReadOnlyList<DocChunk> results;

        if (library != null)
        {
            results = await chunkRepository.FindByQualifiedNameAsync(library,
                                                                     resolvedVersion ?? string.Empty,
                                                                     className,
                                                                     ct
                                                                    );
        }
        else
        {
            var libraries = await libraryRepository.GetAllLibrariesAsync(ct);
            var allResults = new List<DocChunk>();
            foreach(var lib in libraries)
            {
                var publishedVersion = await LibraryTools.ResolveVersionAsync(libraryRepository,
                                                                               lib.Id,
                                                                               version: null,
                                                                               ct);
                if (publishedVersion != null)
                {
                    var chunks = await chunkRepository.FindByQualifiedNameAsync(lib.Id,
                                                                                 publishedVersion,
                                                                                 className,
                                                                                 ct);
                    allResults.AddRange(chunks);
                }
            }

            results = allResults;
        }

        return results;
    }

    private static async Task<string?> ResolveIfNeeded(ILibraryRepository libraryRepository,
                                                       string? library,
                                                       string? version,
                                                       CancellationToken ct)
    {
        string? result = null;
        if (library != null)
            result = await LibraryTools.ResolveVersionAsync(libraryRepository, library, version, ct);
        return result;
    }

    private static async Task<string> ExecuteSearchAsync(IVectorSearchProvider vectorSearch,
                                                         IEmbeddingProvider embeddingProvider,
                                                         IReRanker reRanker,
                                                         RepositoryFactory repositoryFactory,
                                                         RankingSettings rankingSettings,
                                                         IQueryMetrics metrics,
                                                         ILogger<SearchToolsLog> logger,
                                                         string query,
                                                         string? library,
                                                         string? resolvedVersion,
                                                         string? category,
                                                         SubjectSearchContext subjectContext,
                                                         int maxResults,
                                                         string? profile,
                                                         CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();

        DocCategory? categoryFilter = null;
        if (!string.IsNullOrEmpty(category) && Enum.TryParse<DocCategory>(category, ignoreCase: true, out var parsed))
            categoryFilter = parsed;

        var queryIsIdentifierShape = QueryShapeClassifier.IsIdentifierShaped(query);

        var embedSw = Stopwatch.StartNew();
        var embeddings = await metrics.TimeAsync(QueryMetricOperations.EmbedQuery,
                                                 () => embeddingProvider.EmbedAsync([query], EmbedRole.Query, ct),
                                                 note: $"library={library ?? "*"}"
                                                );
        embedSw.Stop();

        var vectorSw = Stopwatch.StartNew();
        var candidateCount = ResolveVectorCandidateCount(maxResults, rankingSettings);
        var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
        var searchResults = await metrics.TimeAsync(QueryMetricOperations.VectorSearch,
                                                    () => SearchPublishedVectorsAsync(vectorSearch,
                                                             libraryRepository,
                                                             embeddings[0],
                                                             library,
                                                             resolvedVersion,
                                                             categoryFilter,
                                                             subjectContext.ExplicitSubjectId,
                                                             candidateCount,
                                                             profile,
                                                             ct),
                                                    r => r.Count,
                                                    $"library={library ?? "*"}"
                                                   );
        vectorSw.Stop();

        var bm25Sw = Stopwatch.StartNew();
        var bm25Scores = await GetBm25ScoresAsync(repositoryFactory,
                                                  library,
                                                  resolvedVersion,
                                                  profile,
                                                  query,
                                                  ct
                                                 );
        bm25Sw.Stop();

        var hybrid = BlendVectorAndBm25(searchResults,
                                        bm25Scores,
                                        rankingSettings.Bm25Weight,
                                        subjectContext.InferredSubjectId);

        // Identifier-shape fast path is an enhancement over the hybrid
        // pool — skipped on cross-library queries to avoid noisy
        // near-matches from many libraries at once. Failures degrade to
        // hybrid-only via InjectIdentifierMatchesOrFallbackAsync.
        if (queryIsIdentifierShape && library != null && resolvedVersion != null)
        {
            var chunkRepository = repositoryFactory.GetChunkRepository(profile);
            hybrid = await InjectIdentifierMatchesOrFallbackAsync(hybrid,
                                                                  chunkRepository,
                                                                  query,
                                                                  library,
                                                                  resolvedVersion,
                                                                  logger,
                                                                  metrics,
                                                                  subjectContext.ExplicitSubjectId,
                                                                  ct
                                                                 );
        }

        var rerankActive = ShouldRerank(rankingSettings.ReRankerStrategy, queryIsIdentifierShape, hybrid.Count);
        var reRankCandidateCount = rerankActive ? ResolveReRankCandidateCount(hybrid.Count, rankingSettings) : 0;

        var rerankSw = Stopwatch.StartNew();
        var ranked = await ApplyRerankerOrPassThroughAsync(reRanker,
                                                           metrics,
                                                           query,
                                                           hybrid,
                                                           maxResults,
                                                           rerankActive,
                                                           rankingSettings.MaxReRankCandidates,
                                                           ct
                                                          );
        rerankSw.Stop();

        IReadOnlyDictionary<string, SubjectSearchMetadata> subjectMetadata = smEmptySubjectMetadata;
        if (ranked.Any(result => result.Chunk.DocumentSource != null &&
                                result.Chunk.SubjectTaxonomyVersion != null))
        {
            var subjectEnricher = new SubjectSearchEnricher(
                repositoryFactory.GetSubjectAssignmentRepository(profile),
                repositoryFactory.GetSubjectCatalogRepository(profile));
            subjectMetadata = await subjectEnricher.EnrichAsync(ranked.Select(result => result.Chunk).ToList(), ct);
        }
        totalSw.Stop();

        var json = SerializeSearchResponse(ranked,
                                           embedSw,
                                           vectorSw,
                                           bm25Sw,
                                           rerankSw,
                                           totalSw,
                                           hybrid.Count,
                                           reRankCandidateCount,
                                           rerankActive,
                                           queryIsIdentifierShape,
                                           categoryFilter,
                                           subjectContext,
                                           subjectMetadata,
                                           rankingSettings
                                          );
        return json;
    }

    private static async Task<IReadOnlyList<VectorSearchResult>> SearchPublishedVectorsAsync(
        IVectorSearchProvider vectorSearch,
        ILibraryRepository libraryRepository,
        float[] queryEmbedding,
        string? library,
        string? resolvedVersion,
        DocCategory? category,
        string? explicitSubjectId,
        int candidateCount,
        string? profile,
        CancellationToken ct)
    {
        IReadOnlyList<VectorSearchResult> result;
        if (library != null)
        {
            if (resolvedVersion == null)
                result = [];
            else
            {
                var exactFilter = new VectorSearchFilter
                                      {
                                          Profile = profile,
                                          LibraryId = library,
                                          Version = resolvedVersion,
                                          Category = category,
                                          SubjectId = explicitSubjectId
                                      };
                result = await vectorSearch.SearchAsync(queryEmbedding, exactFilter, candidateCount, ct);
            }
        }
        else
        {
            var libraries = await libraryRepository.GetAllLibrariesAsync(ct);
            var combined = new List<VectorSearchResult>();
            foreach(var currentLibrary in libraries)
            {
                var publishedVersion = await LibraryTools.ResolveVersionAsync(libraryRepository,
                                                                               currentLibrary.Id,
                                                                               currentLibrary.CurrentVersion,
                                                                               ct);
                if (publishedVersion != null)
                {
                    var filter = new VectorSearchFilter
                                     {
                                         Profile = profile,
                                         LibraryId = currentLibrary.Id,
                                         Version = publishedVersion,
                                         Category = category,
                                         SubjectId = explicitSubjectId
                                     };
                    var results = await vectorSearch.SearchAsync(queryEmbedding, filter, candidateCount, ct);
                    combined.AddRange(results.Where(r => r.Chunk.LibraryId == currentLibrary.Id &&
                                                         r.Chunk.Version == publishedVersion));
                }
            }

            result = combined.OrderByDescending(r => r.Score).Take(candidateCount).ToList();
        }

        return result;
    }

    private static async Task<SubjectSearchContext> ResolveSubjectContextAsync(
        RepositoryFactory repositoryFactory,
        ILibraryRepository libraryRepository,
        string? library,
        string? resolvedVersion,
        string? explicitSubject,
        string query,
        string? profile,
        CancellationToken ct)
    {
        var result = new SubjectSearchContext();
        if (library == null)
            EnsureNoCrossLibrarySubjectFilter(explicitSubject);
        else
            result = await ResolveScopedSubjectContextAsync(repositoryFactory,
                                                            libraryRepository,
                                                            library,
                                                            resolvedVersion,
                                                            explicitSubject,
                                                            query,
                                                            profile,
                                                            ct);

        return result;
    }

    private static async Task<SubjectSearchContext> ResolveScopedSubjectContextAsync(
        RepositoryFactory repositoryFactory,
        ILibraryRepository libraryRepository,
        string library,
        string? resolvedVersion,
        string? explicitSubject,
        string query,
        string? profile,
        CancellationToken ct)
    {
        var result = new SubjectSearchContext();
        if (resolvedVersion != null)
        {
            LibraryVersionRecord? version = await libraryRepository.GetVersionAsync(library, resolvedVersion, ct);
            if (string.IsNullOrEmpty(version?.SubjectTaxonomyVersion))
                EnsureSubjectIsOptional(explicitSubject,
                                        $"Library '{library}' version '{resolvedVersion}' has no subject catalog.");
            else
                result = await ResolveCatalogSubjectContextAsync(repositoryFactory,
                                                                 library,
                                                                 version.SubjectTaxonomyVersion,
                                                                 explicitSubject,
                                                                 query,
                                                                 profile,
                                                                 ct);
        }

        return result;
    }

    private static async Task<SubjectSearchContext> ResolveCatalogSubjectContextAsync(
        RepositoryFactory repositoryFactory,
        string library,
        string taxonomyVersion,
        string? explicitSubject,
        string query,
        string? profile,
        CancellationToken ct)
    {
        var catalogRepository = repositoryFactory.GetSubjectCatalogRepository(profile);
        SubjectCatalogRecord? catalog = await catalogRepository.GetAsync(library, taxonomyVersion, ct);
        var result = new SubjectSearchContext();
        if (catalog == null)
            EnsureSubjectIsOptional(explicitSubject,
                                    $"Subject catalog '{taxonomyVersion}' is unavailable for library '{library}'.");
        else
            result = BuildSubjectSearchContext(catalog, explicitSubject, query, library);
        return result;
    }

    private static SubjectSearchContext BuildSubjectSearchContext(SubjectCatalogRecord catalog,
                                                                  string? explicitSubject,
                                                                  string query,
                                                                  string library)
    {
        string? explicitSubjectId = null;
        string? inferredSubjectId = null;
        if (!string.IsNullOrWhiteSpace(explicitSubject))
        {
            explicitSubjectId = SubjectSearchPolicy.ResolveExplicit(catalog, explicitSubject);
            if (explicitSubjectId == null)
                throw new InvalidDataException($"Subject '{explicitSubject}' was not found in library '{library}'.");
        }
        else
            inferredSubjectId = SubjectSearchPolicy.Infer(catalog, query);

        return new SubjectSearchContext
                   {
                       ExplicitSubjectId = explicitSubjectId,
                       InferredSubjectId = inferredSubjectId,
                       Catalog = catalog
                   };
    }

    private static void EnsureNoCrossLibrarySubjectFilter(string? explicitSubject)
    {
        if (!string.IsNullOrWhiteSpace(explicitSubject))
            throw new InvalidDataException("An explicit subject filter requires a library because subject catalogs are library-scoped.");
    }

    private static void EnsureSubjectIsOptional(string? explicitSubject, string error)
    {
        if (!string.IsNullOrWhiteSpace(explicitSubject))
            throw new InvalidDataException(error);
    }

    private static async Task<IReadOnlyDictionary<string, double>> GetBm25ScoresAsync(
        RepositoryFactory repositoryFactory,
        string? library,
        string? resolvedVersion,
        string? profile,
        string query,
        CancellationToken ct)
    {
        var result = smEmptyBm25Scores;

        if (library != null && resolvedVersion != null)
        {
            var indexRepo = repositoryFactory.GetLibraryIndexRepository(profile);
            var bm25ShardRepo = repositoryFactory.GetBm25ShardRepository(profile);
            var index = await indexRepo.GetAsync(library, resolvedVersion, ct);
            if (index is { Bm25.DocumentCount: > 0 })
            {
                var lookup = new ShardedBm25TermLookup(bm25ShardRepo, library, resolvedVersion, index.Bm25.ShardCount);
                result = await Bm25Scorer.ScoreAsync(lookup, index.Bm25, query, ct);
            }
        }

        return result;
    }

    internal static IReadOnlyList<HybridCandidate> BlendVectorAndBm25(IReadOnlyList<VectorSearchResult> vectorResults,
                                                                     IReadOnlyDictionary<string, double> bm25Scores,
                                                                     float bm25Weight,
                                                                     string? inferredSubjectId)
    {
        ArgumentNullException.ThrowIfNull(vectorResults);
        ArgumentNullException.ThrowIfNull(bm25Scores);
        var maxBm25 = bm25Scores.Count > 0 ? bm25Scores.Values.Max() : 0.0;
        var vectorWeight = 1.0f - bm25Weight;
        var vectorRanks = RankScores(vectorResults.Select(vr =>
            (vr.Chunk.Id, (double) vr.Score + SubjectSearchPolicy.GetBoost(vr.Chunk, inferredSubjectId))));
        // Rank only eligible candidates: scores from other categories, subjects,
        // or pages outside the retrieved pool must not dilute keyword evidence.
        var keywordRanks = RankScores(vectorResults.Where(vr => bm25Scores.GetValueOrDefault(vr.Chunk.Id) > 0)
            .Select(vr => (vr.Chunk.Id, bm25Scores[vr.Chunk.Id])));

        var blended = vectorResults
                      .Select(vr => BuildHybridCandidate(vr,
                                                         bm25Scores,
                                                         maxBm25,
                                                         bm25Weight,
                                                         vectorWeight,
                                                         inferredSubjectId,
                                                         vectorRanks[vr.Chunk.Id],
                                                         keywordRanks.GetValueOrDefault(vr.Chunk.Id)))
                      .OrderByDescending(c => c.HybridScore)
                      .ToList();
        return blended;
    }

    private static HybridCandidate BuildHybridCandidate(VectorSearchResult vr,
                                                        IReadOnlyDictionary<string, double> bm25Scores,
                                                        double maxBm25,
                                                        float bm25Weight,
                                                        float vectorWeight,
                                                        string? inferredSubjectId,
                                                        int vectorRank,
                                                        int keywordRank)
    {
        var bm25 = bm25Scores.TryGetValue(vr.Chunk.Id, out var s) ? s : 0.0;
        var bm25Norm = maxBm25 > 0 ? bm25 / maxBm25 : 0.0;
        double subjectBoost = SubjectSearchPolicy.GetBoost(vr.Chunk, inferredSubjectId);
        var hybrid = (vectorWeight * ReciprocalRank(vectorRank)) +
                     (bm25Weight * ReciprocalRank(keywordRank));
        var result = new HybridCandidate
                         {
                             Chunk = vr.Chunk,
                             VectorScore = vr.Score,
                             Bm25Score = bm25Norm,
                             SubjectBoost = subjectBoost,
                             HybridScore = hybrid
                         };
        return result;
    }

    private static Dictionary<string, int> RankScores(IEnumerable<(string Id, double Score)> scores)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        double? previousScore = null;
        var position = 0;
        var rank = 0;
        foreach(var candidate in scores.OrderByDescending(candidate => candidate.Score))
        {
            position++;
            // Use the same exact ordering comparison as OrderByDescending:
            // equal sort keys share a rank; nearby distinct scores do not.
            if (!previousScore.HasValue || candidate.Score.CompareTo(previousScore.Value) != 0)
                rank = position;
            ranks.Add(candidate.Id, rank);
            previousScore = candidate.Score;
        }

        return ranks;
    }

    private static double ReciprocalRank(int rank) => rank > 0 ? 1.0 / rank : 0.0;

    private static bool ShouldRerank(ReRankerStrategy strategy, bool queryIsIdentifierShape, int candidateCount)
    {
        // queryIsIdentifierShape is still computed and surfaced in the
        // diagnostic Strategy field but no longer gates dispatch — the
        // ONNX cross-encoder scores exact-name matches at 0.9+ and lifts
        // them above noise on identifier queries.
        _ = queryIsIdentifierShape;
        var enoughCandidates = candidateCount >= ReRankMinCandidates;

        var result = (strategy, enoughCandidates) switch
            {
                (ReRankerStrategy.Off, var _) => false,
                (ReRankerStrategy.Onnx, false) => false,
                (ReRankerStrategy.Onnx, true) => true,
                var _ => false
            };
        return result;
    }

    internal static async Task<IReadOnlyList<RankedResult>> ApplyRerankerOrPassThroughAsync(IReRanker reRanker,
        IQueryMetrics metrics,
        string query,
        IReadOnlyList<HybridCandidate> hybrid,
        int maxResults,
        bool rerankActive,
        int maxReRankCandidates,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reRanker);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentException.ThrowIfNullOrEmpty(query);
        ArgumentNullException.ThrowIfNull(hybrid);
        var sourceMatches = DocumentIdentitySearchPolicy.FindMatches(query,
            hybrid.Where(candidate => candidate.HybridScore > 0).Select(candidate => candidate.Chunk));
        IReadOnlyList<RankedResult> result;

        if (!rerankActive)
        {
            result = SelectDistinctResults(hybrid.Select(c => new RankedResult
                                            {
                                                Chunk = c.Chunk,
                                                FinalScore = (float) c.HybridScore + SourceIdentityPriority(sourceMatches, c.Chunk.Id),
                                                SourceIdentityMatch = sourceMatches.Contains(c.Chunk.Id),
                                                VectorScore = c.VectorScore,
                                                Bm25Score = (float) c.Bm25Score,
                                                RerankScore = null
                                            }
                                  ), maxResults);
        }
        else
        {
            result = await ApplyRerankerOrderingAsync(reRanker,
                                                     metrics,
                                                     query,
                                                     hybrid,
                                                     maxResults,
                                                     maxReRankCandidates,
                                                     sourceMatches,
                                                     ct
                                                    );
        }

        return result;
    }

    /// <summary>
    ///     Give retrieval and the local reranker equal votes using reciprocal
    ///     ranks. A first-place vote stays useful even when the other method
    ///     disagrees. Unscored candidates receive only their retrieval vote;
    ///     raw model scores are retained for diagnostics, never mixed with
    ///     retrieval scores. Tied scores receive the same rank.
    /// </summary>
    private static async Task<IReadOnlyList<RankedResult>> ApplyRerankerOrderingAsync(IReRanker reRanker,
        IQueryMetrics metrics,
        string query,
        IReadOnlyList<HybridCandidate> hybrid,
        int maxResults,
        int maxReRankCandidates,
        IReadOnlySet<string> sourceMatches,
        CancellationToken ct)
    {
        var hybridByChunkId = hybrid.ToDictionary(c => c.Chunk.Id, c => c, StringComparer.Ordinal);
        var reRankCandidateCount = ResolveReRankCandidateCount(hybrid.Count, maxReRankCandidates);
        var reRankCandidates = hybrid.OrderByDescending(c => sourceMatches.Contains(c.Chunk.Id))
                                     .Take(reRankCandidateCount).ToList();
        var candidateChunks = reRankCandidates.Select(c => c.Chunk).ToList();
        var rerankResults = await metrics.TimeAsync(QueryMetricOperations.Rerank,
                                                    () => reRanker.ReRankAsync(query,
                                                                               candidateChunks,
                                                                               reRankCandidateCount,
                                                                               ct
                                                                              ),
                                                    r => r.Count
                                                   );

        var rerankedById = rerankResults.Where(rr => hybridByChunkId.ContainsKey(rr.Chunk.Id))
                                       .DistinctBy(rr => rr.Chunk.Id)
                                       .ToDictionary(rr => rr.Chunk.Id, StringComparer.Ordinal);
        var retrievalRanks = RankScores(hybrid.Where(candidate => candidate.HybridScore > 0)
                                             .Select(candidate => (candidate.Chunk.Id, candidate.HybridScore)));
        var rerankRanks = RankScores(rerankedById.Values.Select(rr => (rr.Chunk.Id, (double) rr.RelevanceScore)));
        var ordered = SelectDistinctResults(hybrid.Select(candidate => CreateReRankedResult(candidate,
                                                                      retrievalRanks,
                                                                      rerankRanks,
                                                                      rerankedById,
                                                                      sourceMatches)), maxResults);
        return ordered;
    }

    private static IReadOnlyList<RankedResult> SelectDistinctResults(IEnumerable<RankedResult> candidates, int maxResults)
    {
        // Alternate web URLs can contain exactly the same passage. Keep the
        // highest-ranked copy and its citation before applying the result limit,
        // so duplicates cannot crowd a different answer out of the response.
        // Preserve version, section, symbol, and local-document context: equal
        // prose alone does not make two technical references interchangeable.
        var result = candidates.OrderByDescending(candidate => candidate.FinalScore)
                               .DistinctBy(candidate => new
                                                            {
                                                                candidate.Chunk.LibraryId,
                                                                candidate.Chunk.Version,
                                                                candidate.Chunk.Category,
                                                                candidate.Chunk.PageTitle,
                                                                candidate.Chunk.SectionPath,
                                                                candidate.Chunk.QualifiedName,
                                                                candidate.Chunk.CodeLanguage,
                                                                candidate.Chunk.DocumentSource,
                                                                candidate.Chunk.Content
                                                            })
                               .Take(maxResults)
                               .ToList();
        return result;
    }

    private static RankedResult CreateReRankedResult(HybridCandidate candidate,
                                                     IReadOnlyDictionary<string, int> retrievalRanks,
                                                     IReadOnlyDictionary<string, int> rerankRanks,
                                                     IReadOnlyDictionary<string, ReRankResult> rerankedById,
                                                     IReadOnlySet<string> sourceMatches)
    {
        var id = candidate.Chunk.Id;
        var score = RerankerVoteWeight * ReciprocalRank(retrievalRanks.GetValueOrDefault(id)) +
                    RerankerVoteWeight * ReciprocalRank(rerankRanks.GetValueOrDefault(id));
        var result = new RankedResult
                         {
                             Chunk = candidate.Chunk,
                             FinalScore = (float) score + SourceIdentityPriority(sourceMatches, id),
                             SourceIdentityMatch = sourceMatches.Contains(id),
                             VectorScore = candidate.VectorScore,
                             Bm25Score = (float) candidate.Bm25Score,
                             RerankScore = rerankedById.GetValueOrDefault(id)?.RelevanceScore
                         };
        return result;
    }

    private const double RerankerVoteWeight = 0.5;

    // Rank-fusion scores are bounded by one. Adding one encodes source
    // preference without changing the order within either group. Candidates
    // remain available when metadata is missing or the query is ambiguous.
    private static float SourceIdentityPriority(IReadOnlySet<string> matches, string id) => matches.Contains(id) ? 1f : 0f;

    private static string SerializeSearchResponse(IReadOnlyList<RankedResult> ranked,
                                                  Stopwatch embedSw,
                                                  Stopwatch vectorSw,
                                                  Stopwatch bm25Sw,
                                                  Stopwatch rerankSw,
                                                  Stopwatch totalSw,
                                                  int candidateCount,
                                                  int reRankCandidateCount,
                                                  bool rerankActive,
                                                  bool queryIsIdentifierShape,
                                                  DocCategory? categoryFilter,
                                                  SubjectSearchContext subjectContext,
                                                  IReadOnlyDictionary<string, SubjectSearchMetadata> subjectMetadata,
                                                  RankingSettings rankingSettings)
    {
        var results = new JsonArray();
        foreach(RankedResult rankedResult in ranked)
        {
            JsonNode serializedResult = JsonSerializer.SerializeToNode(new
                                                                           {
                                                                               rankedResult.Chunk.LibraryId,
                                                                               rankedResult.Chunk.Category,
                                                                               rankedResult.Chunk.PageTitle,
                                                                               rankedResult.Chunk.SectionPath,
                                                                               rankedResult.Chunk.PageUrl,
                                                                               rankedResult.Chunk.Content,
                                                                               rankedResult.Chunk.QualifiedName,
                                                                               rankedResult.Chunk.CodeLanguage,
                                                                               RelevanceScore = rankedResult.FinalScore,
                                                                               rankedResult.VectorScore,
                                                                               rankedResult.Bm25Score,
                                                                               rankedResult.RerankScore,
                                                                               rankedResult.SourceIdentityMatch
                                                                           },
                                                                       smJsonOptions)
                                        ?? throw new InvalidOperationException("Search result serialization returned no JSON value.");
            JsonObject result = serializedResult.AsObject();
            if (subjectMetadata.TryGetValue(rankedResult.Chunk.Id, out SubjectSearchMetadata? metadata))
            {
                result[nameof(DocChunk.SubjectTaxonomyVersion)] = metadata.TaxonomyVersion;
                result[nameof(SubjectSearchMetadata.NeedsReview)] = metadata.NeedsReview;
                result[nameof(SubjectSearchMetadata.Subjects)] = JsonSerializer.SerializeToNode(metadata.Subjects,
                                                                                                 smJsonOptions);
            }

            if (rankedResult.Chunk.DocumentSource != null)
            {
                result[nameof(DocChunk.DocumentSource)] =
                    JsonSerializer.SerializeToNode(rankedResult.Chunk.DocumentSource, smJsonOptions);
            }

            results.Add(result);
        }

        JsonNode serializedStrategy = JsonSerializer.SerializeToNode(new
                                                                         {
                                                                             ReRankerStrategy = rankingSettings.ReRankerStrategy.ToString(),
                                                                             RerankActive = rerankActive,
                                                                             QueryIsIdentifierShape = queryIsIdentifierShape,
                                                                             Category = categoryFilter?.ToString(),
                                                                             rankingSettings.Bm25Weight
                                                                         },
                                                                     smJsonOptions)
                                      ?? throw new InvalidOperationException("Search strategy serialization returned no JSON value.");
        JsonObject strategy = serializedStrategy.AsObject();
        if (subjectContext.ExplicitSubjectId != null)
            strategy[nameof(SubjectSearchContext.ExplicitSubjectId)] = subjectContext.ExplicitSubjectId;
        if (subjectContext.InferredSubjectId != null)
            strategy[nameof(SubjectSearchContext.InferredSubjectId)] = subjectContext.InferredSubjectId;

        var response = new JsonObject
                           {
                               ["Results"] = results,
                               ["Timing"] = JsonSerializer.SerializeToNode(new
                                                                               {
                                                                                   EmbedMs = embedSw.ElapsedMilliseconds,
                                                                                   VectorSearchMs = vectorSw.ElapsedMilliseconds,
                                                                                   Bm25Ms = bm25Sw.ElapsedMilliseconds,
                                                                                   ReRankMs = rerankSw.ElapsedMilliseconds,
                                                                                   TotalMs = totalSw.ElapsedMilliseconds,
                                                                                   CandidateCount = candidateCount,
                                                                                   ReRankCandidateCount = reRankCandidateCount
                                                                               },
                                                                           smJsonOptions),
                               ["Strategy"] = strategy
                           };
        return response.ToJsonString(smJsonOptions);
    }

    private static string LibraryNotFoundJson(string library)
    {
        var error = new
                        {
                            Error = $"Library '{library}' not found. Use list_libraries to see available libraries, " +
                                    IndexNewLibrariesHint
                        };
        var result = JsonSerializer.Serialize(error, smJsonOptions);
        return result;
    }

    private static string ErrorJson(string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        return JsonSerializer.Serialize(new { Error = message }, smJsonOptions);
    }

    /// <summary>
    ///     Runs <see cref="InjectIdentifierMatchesAsync" /> and swallows
    ///     any non-cancellation exception, returning the original hybrid
    ///     pool with a warning logged. The fast path is an enhancement —
    ///     a transient repository or tokenization failure must not regress
    ///     a search that the hybrid pipeline already answered. Cancellation
    ///     still propagates so the request can shed. Records one
    ///     <c>identifier_fast_path</c> sample on <paramref name="metrics" />
    ///     per call (success with injected-count note, failure with
    ///     exception-type note) so an SLO can alert on degradation that
    ///     the LogLevel.Warning alone wouldn't surface.
    /// </summary>
    internal static async Task<IReadOnlyList<HybridCandidate>> InjectIdentifierMatchesOrFallbackAsync(
        IReadOnlyList<HybridCandidate> hybrid,
        IChunkRepository chunkRepository,
        string query,
        string library,
        string version,
        ILogger<SearchToolsLog> logger,
        IQueryMetrics metrics,
        CancellationToken ct) =>
        await InjectIdentifierMatchesOrFallbackAsync(hybrid,
                                                     chunkRepository,
                                                     query,
                                                     library,
                                                     version,
                                                     logger,
                                                     metrics,
                                                     explicitSubjectId: null,
                                                     ct: ct);

    internal static async Task<IReadOnlyList<HybridCandidate>> InjectIdentifierMatchesOrFallbackAsync(
        IReadOnlyList<HybridCandidate> hybrid,
        IChunkRepository chunkRepository,
        string query,
        string library,
        string version,
        ILogger<SearchToolsLog> logger,
        IQueryMetrics metrics,
        string? explicitSubjectId,
        CancellationToken ct)
    {
        IReadOnlyList<HybridCandidate> result;
        var sw = Stopwatch.StartNew();

        try
        {
            result = await InjectIdentifierMatchesAsync(hybrid,
                                                        chunkRepository,
                                                        query,
                                                        library,
                                                        version,
                                                        explicitSubjectId,
                                                        ct);
            sw.Stop();
            // Injected-count = result count minus pre-existing hybrid count.
            // Zero is a valid success (no QualifiedName match for any token).
            var injected = result.Count - hybrid.Count;
            metrics.Record(QueryMetricOperations.IdentifierFastPath,
                           sw.Elapsed,
                           success: true,
                           resultCount: injected,
                           note: $"library={library}"
                          );
        }
        catch(Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            logger.LogWarning(ex,
                              "Identifier fast-path skipped for query={Query}, library={Library}, version={Version}; degrading to hybrid-only results",
                              query,
                              library,
                              version
                             );
            metrics.Record(QueryMetricOperations.IdentifierFastPath,
                           sw.Elapsed,
                           success: false,
                           resultCount: null,
                           note: $"{ex.GetType().Name} library={library}"
                          );
            result = hybrid;
        }

        return result;
    }

    internal static async Task<IReadOnlyList<HybridCandidate>> InjectIdentifierMatchesAsync(
        IReadOnlyList<HybridCandidate> hybrid,
        IChunkRepository chunkRepository,
        string query,
        string library,
        string version,
        CancellationToken ct) =>
        await InjectIdentifierMatchesAsync(hybrid,
                                           chunkRepository,
                                           query,
                                           library,
                                           version,
                                           explicitSubjectId: null,
                                           ct: ct);

    internal static async Task<IReadOnlyList<HybridCandidate>> InjectIdentifierMatchesAsync(
        IReadOnlyList<HybridCandidate> hybrid,
        IChunkRepository chunkRepository,
        string query,
        string library,
        string version,
        string? explicitSubjectId,
        CancellationToken ct)
    {
        var tokens = IdentifierTokenizer.ExtractDistinct(query, MinIdentifierTokenLength);
        var matches = new List<DocChunk>();

        foreach(var token in tokens.Take(MaxIdentifierLookupTokens))
        {
            var chunks = await chunkRepository.FindByQualifiedNameAsync(library, version, token, ct);
            foreach(var chunk in chunks.Where(c => IsExactCaseInsensitiveQualifiedNameMatch(c, token)))
                matches.Add(chunk);
        }

        if (explicitSubjectId != null)
            matches = matches.Where(chunk => chunk.SubjectIds.Contains(explicitSubjectId,
                                                                        StringComparer.Ordinal)).ToList();

        var result = InjectIdentifierMatches(hybrid, matches);
        return result;
    }

    private static bool IsExactCaseInsensitiveQualifiedNameMatch(DocChunk chunk, string token)
    {
        var result = !string.IsNullOrEmpty(chunk.QualifiedName) &&
                     string.Equals(chunk.QualifiedName, token, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>
    ///     Prepend chunks not already present in <paramref name="hybrid" />
    ///     as synthetic <see cref="HybridCandidate" /> rows with zero
    ///     vector, BM25, and hybrid scores. Zero scores are intentional —
    ///     these rows exist solely to enter the rerank slice; with rerank
    ///     disabled they sort to the bottom of the pool. The count is
    ///     capped at <see cref="MaxInjectedIdentifierMatches" /> so a
    ///     query that hits a popular <c>QualifiedName</c> can't crowd
    ///     genuinely top-scored hybrid hits out of the rerank window.
    /// </summary>
    internal static IReadOnlyList<HybridCandidate> InjectIdentifierMatches(
        IReadOnlyList<HybridCandidate> hybrid,
        IReadOnlyList<DocChunk> matches)
    {
        IReadOnlyList<HybridCandidate> result = hybrid;

        if (matches.Count > 0)
        {
            var existing = hybrid.Select(c => c.Chunk.Id).ToHashSet(StringComparer.Ordinal);
            var injected = matches
                          .DistinctBy(c => c.Id, StringComparer.Ordinal)
                          .Where(c => !existing.Contains(c.Id))
                          .Take(MaxInjectedIdentifierMatches)
                          .Select(c => new HybridCandidate
                                           {
                                               Chunk = c,
                                               VectorScore = 0f,
                                               Bm25Score = 0.0,
                                               HybridScore = 0.0
                                           }
                                 )
                          .ToList();
            if (injected.Count > 0)
                result = injected.Concat(hybrid).ToList();
        }

        return result;
    }

    private static int ResolveVectorCandidateCount(int maxResults, RankingSettings rankingSettings)
    {
        var multiplier = Math.Max(MinimumCandidateMultiplier, rankingSettings.VectorCandidateMultiplier);
        var minCount = Math.Max(maxResults, rankingSettings.MinVectorCandidateCount);
        var result = Math.Max(minCount, maxResults * multiplier);
        return result;
    }

    private static int ResolveReRankCandidateCount(int candidateCount, RankingSettings rankingSettings)
    {
        var result = ResolveReRankCandidateCount(candidateCount, rankingSettings.MaxReRankCandidates);
        return result;
    }

    private static int ResolveReRankCandidateCount(int candidateCount, int maxReRankCandidates)
    {
        var allowedCount = Math.Max(MinimumCandidateCount, maxReRankCandidates);
        var result = Math.Min(candidateCount, allowedCount);
        return result;
    }

    private const string IndexNewLibrariesHint = "or scrape_docs/index_project_dependencies to index new ones.";
    private const int ReRankMinCandidates = 6;
    private const int MinimumCandidateCount = 1;
    private const int MinimumCandidateMultiplier = 2;
    private const int MaxOverviewResults = 5;
    private const int MinIdentifierTokenLength = 2;
    private const int MaxIdentifierLookupTokens = 4;
    private const int MaxInjectedIdentifierMatches = 5;
    private static readonly IReadOnlyDictionary<string, double> smEmptyBm25Scores =
        new Dictionary<string, double>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, SubjectSearchMetadata> smEmptySubjectMetadata =
        new Dictionary<string, SubjectSearchMetadata>(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
