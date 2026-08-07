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
///     MCP tools for searching documentation content. Uses hybrid scoring
///     (vector cosine ∥ BM25 keyword overlap) with an optional LLM
///     reranker. The reranker score is blended with hybrid (not replaced),
///     so the reranker's mistakes stay recoverable.
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

    private record RankedResult
    {
        public required DocChunk Chunk { get; init; }
        public required float FinalScore { get; init; }
        public required float VectorScore { get; init; }
        public required float Bm25Score { get; init; }
        public float? RerankScore { get; init; }
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

    private static IReadOnlyList<HybridCandidate> BlendVectorAndBm25(IReadOnlyList<VectorSearchResult> vectorResults,
                                                                     IReadOnlyDictionary<string, double> bm25Scores,
                                                                     float bm25Weight,
                                                                     string? inferredSubjectId)
    {
        var maxBm25 = bm25Scores.Count > 0 ? bm25Scores.Values.Max() : 0.0;
        var vectorWeight = 1.0f - bm25Weight;

        var blended = vectorResults
                      .Select(vr => BuildHybridCandidate(vr,
                                                         bm25Scores,
                                                         maxBm25,
                                                         bm25Weight,
                                                         vectorWeight,
                                                         inferredSubjectId))
                      .OrderByDescending(c => c.HybridScore)
                      .ToList();
        return blended;
    }

    private static HybridCandidate BuildHybridCandidate(VectorSearchResult vr,
                                                        IReadOnlyDictionary<string, double> bm25Scores,
                                                        double maxBm25,
                                                        float bm25Weight,
                                                        float vectorWeight,
                                                        string? inferredSubjectId)
    {
        var bm25 = bm25Scores.TryGetValue(vr.Chunk.Id, out var s) ? s : 0.0;
        var bm25Norm = maxBm25 > 0 ? bm25 / maxBm25 : 0.0;
        double subjectBoost = SubjectSearchPolicy.GetBoost(vr.Chunk, inferredSubjectId);
        var hybrid = (vectorWeight * vr.Score) + (bm25Weight * bm25Norm) + subjectBoost;
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

    private static async Task<IReadOnlyList<RankedResult>> ApplyRerankerOrPassThroughAsync(IReRanker reRanker,
        IQueryMetrics metrics,
        string query,
        IReadOnlyList<HybridCandidate> hybrid,
        int maxResults,
        bool rerankActive,
        int maxReRankCandidates,
        CancellationToken ct)
    {
        IReadOnlyList<RankedResult> result;

        if (!rerankActive)
        {
            result = hybrid.Take(maxResults)
                           .Select(c => new RankedResult
                                            {
                                                Chunk = c.Chunk,
                                                FinalScore = (float) c.HybridScore,
                                                VectorScore = c.VectorScore,
                                                Bm25Score = (float) c.Bm25Score,
                                                RerankScore = null
                                            }
                                  )
                           .ToList();
        }
        else
        {
            result = await ApplyRerankerOrderingAsync(reRanker,
                                                     metrics,
                                                     query,
                                                     hybrid,
                                                     maxResults,
                                                     maxReRankCandidates,
                                                     ct
                                                    );
        }

        return result;
    }

    /// <summary>
    ///     Cross-encoder is authoritative on the candidates it scored.
    ///     The top <paramref name="maxReRankCandidates" /> hybrid items
    ///     are fed to the reranker and ordered by its score
    ///     (sigmoid-mapped logits ∈ (0, 1), see
    ///     <see cref="OnnxReRanker.NormalizeLogit" />). The pass-through
    ///     tail keeps its hybrid score and is appended below the reranked
    ///     tier — never interleaved. This replaces the pre-Phase-4 linear
    ///     blend, which mixed unbounded raw logits with [0, 1] hybrid
    ///     scores and let pass-through items leapfrog low-rerank items
    ///     into the top-N.
    /// </summary>
    private static async Task<IReadOnlyList<RankedResult>> ApplyRerankerOrderingAsync(IReRanker reRanker,
        IQueryMetrics metrics,
        string query,
        IReadOnlyList<HybridCandidate> hybrid,
        int maxResults,
        int maxReRankCandidates,
        CancellationToken ct)
    {
        var hybridByChunkId = hybrid.ToDictionary(c => c.Chunk.Id, c => c, StringComparer.Ordinal);
        var reRankCandidateCount = ResolveReRankCandidateCount(hybrid.Count, maxReRankCandidates);
        var reRankCandidates = hybrid.Take(reRankCandidateCount).ToList();
        var candidateChunks = reRankCandidates.Select(c => c.Chunk).ToList();
        var rerankResults = await metrics.TimeAsync(QueryMetricOperations.Rerank,
                                                    () => reRanker.ReRankAsync(query,
                                                                               candidateChunks,
                                                                               reRankCandidateCount,
                                                                               ct
                                                                              ),
                                                    r => r.Count
                                                   );

        var reranked = rerankResults
                      .Select(rr => CreateReRankedResult(rr, hybridByChunkId))
                      .OrderByDescending(r => r.FinalScore);
        var passThrough = hybrid.Skip(reRankCandidateCount)
                                .Select(CreatePassThroughRankedResult);
        var ordered = reranked.Concat(passThrough)
                      .Take(maxResults)
                      .ToList();
        return ordered;
    }

    private static RankedResult CreatePassThroughRankedResult(HybridCandidate candidate)
    {
        var result = new RankedResult
                         {
                             Chunk = candidate.Chunk,
                             FinalScore = (float) candidate.HybridScore,
                             VectorScore = candidate.VectorScore,
                             Bm25Score = (float) candidate.Bm25Score,
                             RerankScore = null
                         };
        return result;
    }

    private static RankedResult CreateReRankedResult(ReRankResult rr,
                                                     IReadOnlyDictionary<string, HybridCandidate> hybridByChunkId)
    {
        var hybridCandidate = hybridByChunkId.TryGetValue(rr.Chunk.Id, out var hc) ? hc : null;
        var vectorScore = hybridCandidate?.VectorScore ?? 0f;
        var bm25Score = hybridCandidate != null ? (float) hybridCandidate.Bm25Score : 0f;

        var result = new RankedResult
                         {
                             Chunk = rr.Chunk,
                             FinalScore = rr.RelevanceScore,
                             VectorScore = vectorScore,
                             Bm25Score = bm25Score,
                             RerankScore = rr.RelevanceScore
                         };
        return result;
    }

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
                                                                               rankedResult.RerankScore
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
