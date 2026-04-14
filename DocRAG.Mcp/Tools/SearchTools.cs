// // SearchTools.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

/// <summary>
///     MCP tools for searching documentation content via vector similarity.
/// </summary>
[McpServerToolType]
public static class SearchTools
{
    [McpServerTool(Name = "search_docs")]
    [Description("Search documentation using natural language. Works across all ingested libraries " +
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
                                                [Description("Natural language search query")]
                                                string query,
                                                [Description("Library identifier â€” omit to search all libraries")]
                                                string? library = null,
                                                [Description("Filter to category: Overview, HowTo, Sample, ApiReference, ChangeLog"
                                                            )]
                                                string? category = null,
                                                [Description("Specific version â€” defaults to current")]
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
        ArgumentException.ThrowIfNullOrEmpty(query);

        var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
        var resolvedVersion = await ResolveIfNeeded(libraryRepository, library, version, ct);
        string json;

        if (library != null && resolvedVersion == null)
            json = LibraryNotFoundJson(library);
        else
        {
            json = await ExecuteSearchAsync(vectorSearch,
                                            embeddingProvider,
                                            reRanker,
                                            query,
                                            library,
                                            resolvedVersion,
                                            category,
                                            maxResults,
                                            profile,
                                            ct
                                           );
        }

        return json;
    }

    [McpServerTool(Name = "get_class_reference")]
    [Description("Look up API reference for a specific class or type. " +
                 "If library is omitted, searches across ALL libraries. " +
                 "Tries exact match first, then fuzzy match."
                )]
    public static async Task<string> GetClassReference(RepositoryFactory repositoryFactory,
                                                       [Description("Class name (partial or full)")]
                                                       string className,
                                                       [Description("Library identifier â€” omit to search all libraries"
                                                                   )]
                                                       string? library = null,
                                                       [Description("Specific version â€” defaults to current")]
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
            IReadOnlyList<DocChunk> results;

            if (library != null)
                results = await chunkRepository.FindByQualifiedNameAsync(library,
                                                                         resolvedVersion ?? string.Empty,
                                                                         className,
                                                                         ct
                                                                        );
            else
            {
                var libraries = await libraryRepository.GetAllLibrariesAsync(ct);
                var allResults = new List<DocChunk>();
                foreach(var lib in libraries)
                {
                    var chunks =
                        await chunkRepository.FindByQualifiedNameAsync(lib.Id, lib.CurrentVersion, className, ct);
                    allResults.AddRange(chunks);
                }

                results = allResults;
            }

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
    [Description("Get an overview of what a library is and how to get started. " +
                 "Returns Overview-category documentation chunks. " +
                 "If no Overview content exists, returns the most relevant chunks of any category."
                )]
    public static async Task<string> GetLibraryOverview(IVectorSearchProvider vectorSearch,
                                                        IEmbeddingProvider embeddingProvider,
                                                        RepositoryFactory repositoryFactory,
                                                        [Description("Library identifier")] string library,
                                                        [Description("Specific version â€” defaults to current")]
                                                        string? version = null,
                                                        [Description("Optional database profile name")]
                                                        string? profile = null,
                                                        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vectorSearch);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
        var resolvedVersion = await LibraryTools.ResolveVersionAsync(libraryRepository, library, version, ct);
        string json;

        if (resolvedVersion == null)
            json = LibraryNotFoundJson(library);
        else
        {
            var query = $"{library} overview getting started introduction";
            var embeddings = await embeddingProvider.EmbedAsync([query], ct);

            var overviewFilter = new VectorSearchFilter
                                     {
                                         Profile = profile,
                                         LibraryId = library,
                                         Version = resolvedVersion,
                                         Category = DocCategory.Overview
                                     };

            var results = await vectorSearch.SearchAsync(embeddings[0], overviewFilter, MaxOverviewResults, ct);

            if (results.Count == 0)
            {
                var fallbackFilter = new VectorSearchFilter
                                         {
                                             Profile = profile,
                                             LibraryId = library,
                                             Version = resolvedVersion
                                         };
                results = await vectorSearch.SearchAsync(embeddings[0], fallbackFilter, MaxOverviewResults, ct);
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

            json = JsonSerializer.Serialize(response, smJsonOptions);
        }

        return json;
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
                                                         string query,
                                                         string? library,
                                                         string? resolvedVersion,
                                                         string? category,
                                                         int maxResults,
                                                         string? profile,
                                                         CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();

        DocCategory? categoryFilter = null;
        if (!string.IsNullOrEmpty(category) && Enum.TryParse<DocCategory>(category, ignoreCase: true, out var parsed))
            categoryFilter = parsed;

        var embedSw = Stopwatch.StartNew();
        var embeddings = await embeddingProvider.EmbedAsync([query], ct);
        embedSw.Stop();

        var filter = new VectorSearchFilter
                         {
                             Profile = profile,
                             LibraryId = library,
                             Version = resolvedVersion,
                             Category = categoryFilter
                         };

        var vectorSw = Stopwatch.StartNew();
        var candidateCount = maxResults * CandidateMultiplier;
        var searchResults = await vectorSearch.SearchAsync(embeddings[0], filter, candidateCount, ct);
        vectorSw.Stop();

        var rerankSw = Stopwatch.StartNew();
        var candidates = searchResults.Select(r => r.Chunk).ToList();
        IReadOnlyList<ReRankResult> reRanked;

        // Skip re-ranking when there are too few candidates to benefit
        if (candidates.Count >= ReRankMinCandidates)
            reRanked = await reRanker.ReRankAsync(query, candidates, maxResults, ct);
        else
        {
            reRanked = candidates
                       .Select((c, i) => new ReRankResult
                                             {
                                                 Chunk = c,
                                                 RelevanceScore = searchResults[i].Score
                                             }
                              )
                       .Take(maxResults)
                       .ToList();
        }

        rerankSw.Stop();
        totalSw.Stop();

        var results = reRanked.Select(r => new
                                               {
                                                   r.Chunk.LibraryId,
                                                   r.Chunk.Category,
                                                   r.Chunk.PageTitle,
                                                   r.Chunk.SectionPath,
                                                   r.Chunk.PageUrl,
                                                   r.Chunk.Content,
                                                   r.Chunk.QualifiedName,
                                                   r.Chunk.CodeLanguage,
                                                   r.RelevanceScore
                                               }
                                     );

        var response = new
                           {
                               Results = results,
                               Timing = new
                                            {
                                                EmbedMs = embedSw.ElapsedMilliseconds,
                                                VectorSearchMs = vectorSw.ElapsedMilliseconds,
                                                ReRankMs = rerankSw.ElapsedMilliseconds,
                                                TotalMs = totalSw.ElapsedMilliseconds,
                                                CandidateCount = candidates.Count
                                            }
                           };

        var json = JsonSerializer.Serialize(response, smJsonOptions);
        return json;
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

    private const string IndexNewLibrariesHint = "or scrape_docs/index_project_dependencies to index new ones.";
    private const int CandidateMultiplier = 2;
    private const int ReRankMinCandidates = 6;
    private const int MaxOverviewResults = 5;

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
