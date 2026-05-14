// McpWarmupService.cs

// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial

// Available under AGPLv3 (see LICENSE) or a commercial license

// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.


#region Usings

using System.Diagnostics;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Database.Migrations;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Embedding;

#endregion


namespace SaddleRAG.Mcp;

public sealed class McpWarmupService : BackgroundService

{
    public McpWarmupService(IServiceProvider serviceProvider,
                            McpWarmupState warmupState,
                            ILogger<McpWarmupService> logger)

    {
        mServiceProvider = serviceProvider;

        mWarmupState = warmupState;

        mLogger = logger;
    }


    private readonly ILogger<McpWarmupService> mLogger;

    private readonly IServiceProvider mServiceProvider;

    private readonly McpWarmupState mWarmupState;


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)

    {
        var startupSw = Stopwatch.StartNew();

        var stepSw = Stopwatch.StartNew();


        mWarmupState.MarkStarted(PhaseStarting);

        mLogger.LogInformation("[Warmup] T+{Sec:F1}s - Starting", startupSw.Elapsed.TotalSeconds);


        try

        {
            using var scope = mServiceProvider.CreateScope();


            var contextFactory = scope.ServiceProvider.GetRequiredService<SaddleRagDbContextFactory>();

            var dbSettings = scope.ServiceProvider.GetRequiredService<IOptions<SaddleRagDbSettings>>().Value;

            var repositoryFactory = scope.ServiceProvider.GetRequiredService<RepositoryFactory>();

            var bootstrapper = scope.ServiceProvider.GetRequiredService<OllamaBootstrapper>();

            var onnxDownloader = scope.ServiceProvider.GetRequiredService<OnnxModelDownloader>();

            var vectorSearch = scope.ServiceProvider.GetRequiredService<IVectorSearchProvider>();

            // IEmbeddingProvider is resolved *after* EnsureActiveModelsAsync below:
            // OnnxEmbeddingProvider's constructor opens model.onnx + vocab.txt from
            // disk, so on the first start after Onnx.Enabled flips to true the
            // files have to be downloaded first. Resolving here would throw
            // FileNotFoundException before the downloader runs.

            var profileNames = GetProfilesToBootstrap(contextFactory, dbSettings);


            stepSw.Restart();

            var requiredModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach(var profile in profileNames)

            {
                await DiscoverModelsForProfileAsync(profile,
                                                    contextFactory,
                                                    repositoryFactory,
                                                    requiredModels,
                                                    stoppingToken
                                                   );
            }


            mWarmupState.MarkPhase(PhaseMongoDbProfilesDiscovered);

            mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - MongoDB profiles discovered",
                                   startupSw.Elapsed.TotalSeconds,
                                   stepSw.ElapsedMilliseconds
                                  );


            stepSw.Restart();

            await bootstrapper.BootstrapAsync(requiredModels.ToList(), stoppingToken);

            mWarmupState.MarkPhase(PhaseOllamaBootstrapFinished);

            mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - Ollama bootstrap finished",
                                   startupSw.Elapsed.TotalSeconds,
                                   stepSw.ElapsedMilliseconds
                                  );


            stepSw.Restart();

            try

            {
                await onnxDownloader.EnsureActiveModelsAsync(stoppingToken);
            }

            catch(Exception ex) when(ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)

            {
                // Record the specific cause + log at Error, then rethrow.
                // The outer catch detects that state is already Failed and
                // skips its generic MarkFailed/LogError so we don't
                // overwrite PhaseOnnxDownloadFailed or double-log.
                mWarmupState.MarkFailed(PhaseOnnxDownloadFailed, ex.Message);

                mLogger.LogError(ex, OnnxDownloadFailedLogTemplate, startupSw.Elapsed.TotalSeconds);

                throw;
            }

            mWarmupState.MarkPhase(PhaseOnnxModelsReady);

            mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - ONNX models ready",
                                   startupSw.Elapsed.TotalSeconds,
                                   stepSw.ElapsedMilliseconds
                                  );


            var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();


            stepSw.Restart();

            foreach(var profile in profileNames)

                await LoadChunksForProfileAsync(profile, repositoryFactory, vectorSearch, stoppingToken);


            mWarmupState.MarkPhase(PhaseVectorIndicesLoaded);

            mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - Vector indices loaded",
                                   startupSw.Elapsed.TotalSeconds,
                                   stepSw.ElapsedMilliseconds
                                  );


            try

            {
                stepSw.Restart();

                await embeddingProvider.EmbedAsync([WarmupProbeText], ct: stoppingToken);

                mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - embedding provider warm ({Provider}/{Model})",
                                       startupSw.Elapsed.TotalSeconds,
                                       stepSw.ElapsedMilliseconds,
                                       embeddingProvider.ProviderId,
                                       embeddingProvider.ModelName
                                      );


                stepSw.Restart();

                await bootstrapper.WarmModelsAsync(stoppingToken);

                mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - generate models warm",
                                       startupSw.Elapsed.TotalSeconds,
                                       stepSw.ElapsedMilliseconds
                                      );


                stepSw.Restart();

                var warmupEmbedding = (await embeddingProvider.EmbedAsync([WarmupSearchProbeText],
                                                                          EmbedRole.Query,
                                                                          stoppingToken
                                                                         ))[0];

                var warmupFilter = new VectorSearchFilter { Profile = null };

                await vectorSearch.SearchAsync(warmupEmbedding, warmupFilter, maxResults: 1, stoppingToken);

                mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - Full pipeline warm",
                                       startupSw.Elapsed.TotalSeconds,
                                       stepSw.ElapsedMilliseconds
                                      );


                stepSw.Restart();

                var onnxReRanker = scope.ServiceProvider.GetRequiredService<OnnxReRanker>();
                var rankingSettings = scope.ServiceProvider.GetRequiredService<IOptions<RankingSettings>>().Value;

                await WarmReRankerAsync(onnxReRanker, rankingSettings.MaxReRankCandidates, stoppingToken);

                mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - rerank session warm ({Model}, batch={Batch})",
                                       startupSw.Elapsed.TotalSeconds,
                                       stepSw.ElapsedMilliseconds,
                                       string.IsNullOrEmpty(onnxReRanker.ModelName) ? RerankerPassThroughLabel : onnxReRanker.ModelName,
                                       rankingSettings.MaxReRankCandidates
                                      );
            }

            catch(Exception ex) when(ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)

            {
                mLogger.LogWarning(ex, "[Warmup] T+{Sec:F1}s - Warmup probe failed", startupSw.Elapsed.TotalSeconds);
            }


            mWarmupState.MarkCompleted(nameof(ScrapeJobStatus.Completed));

            mLogger.LogInformation("[Warmup] T+{Sec:F1}s - Completed", startupSw.Elapsed.TotalSeconds);
        }

        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)

        {
            mWarmupState.MarkFailed(PhaseCanceled, WarmupCanceledMessage);

            mLogger.LogInformation("[Warmup] T+{Sec:F1}s - Canceled", startupSw.Elapsed.TotalSeconds);
        }

        catch(Exception ex)

        {
            // Skip generic MarkFailed when an inner catch already set a
            // specific phase (e.g., PhaseOnnxDownloadFailed). Otherwise this
            // would overwrite the actionable phase the monitor UI shows
            // and emit a second LogError line for the same incident.
            if (!HasInnerCatchAlreadyMarkedFailure(mWarmupState))
            {
                mWarmupState.MarkFailed(nameof(ScrapeJobStatus.Failed), ex.Message);

                mLogger.LogError(ex, "[Warmup] T+{Sec:F1}s - Failed", startupSw.Elapsed.TotalSeconds);
            }
        }
    }


    private async Task DiscoverModelsForProfileAsync(string? profile,
                                                     SaddleRagDbContextFactory contextFactory,
                                                     RepositoryFactory repositoryFactory,
                                                     HashSet<string> requiredModels,
                                                     CancellationToken stoppingToken)

    {
        try

        {
            var ctx = contextFactory.GetForProfile(profile);

            using var indexCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            indexCts.CancelAfter(TimeSpan.FromSeconds(seconds: 10));

            await ctx.EnsureIndexesAsync(indexCts.Token);

            // One-time migration that folds the four legacy job
            // collections into the unified jobs collection. Idempotent
            // and a no-op once it has run for a profile, so safe to
            // invoke on every startup.
            var jobsMigration = new JobsUnificationMigration(ctx, mLogger);
            await jobsMigration.RunAsync(indexCts.Token);


            var libRepo = repositoryFactory.GetLibraryRepository(profile);

            var libraries = await libRepo.GetAllLibrariesAsync(stoppingToken);

            foreach(var lib in libraries)

            {
                var version = await libRepo.GetVersionAsync(lib.Id, lib.CurrentVersion, stoppingToken);

                string? modelName = ResolveOllamaModelName(version);

                if (modelName != null)

                    requiredModels.Add(modelName);
            }
        }

        catch(Exception ex) when(ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)

        {
            mLogger.LogWarning(ex, "Failed to inspect profile {Profile}, skipping at startup", profile ?? "(default)");
        }
    }


    /// <summary>
    ///     Returns <paramref name="version" />'s
    ///     <c>EmbeddingModelName</c> when the library was embedded under
    ///     Ollama (so it should be added to the set passed to
    ///     <c>OllamaBootstrapper.BootstrapAsync</c>); otherwise null
    ///     (skip). ONNX-embedded libraries persist their Hugging Face
    ///     model name (e.g. <c>"nomic-embed-text-v1.5"</c>) which Ollama's
    ///     manifest lookup returns 404 on — pulling that into the Ollama
    ///     required-models set throws and aborts the entire warmup before
    ///     chunk loading, leaving the in-memory vector index empty. Filter
    ///     to <see cref="OllamaEmbeddingProvider.ProviderIdName" /> here
    ///     so ONNX libraries are skipped silently and only Ollama-embedded
    ///     names reach the bootstrapper. Exposed as <c>internal</c> so
    ///     SaddleRAG.Tests can lock in the contract without spinning up a
    ///     full MongoDB-backed repository graph.
    /// </summary>
    internal static string? ResolveOllamaModelName(LibraryVersionRecord? version)
    {
        string? result = null;

        if (version != null
            && !string.IsNullOrEmpty(version.EmbeddingModelName)
            && string.Equals(version.EmbeddingProviderId,
                             OllamaEmbeddingProvider.ProviderIdName,
                             StringComparison.OrdinalIgnoreCase
                            )
           )
            result = version.EmbeddingModelName;

        return result;
    }


    private async Task LoadChunksForProfileAsync(string? profile,
                                                 RepositoryFactory repositoryFactory,
                                                 IVectorSearchProvider vectorSearch,
                                                 CancellationToken stoppingToken)

    {
        try

        {
            var libRepo = repositoryFactory.GetLibraryRepository(profile);

            var chunkRepo = repositoryFactory.GetChunkRepository(profile);


            var libraries = await libRepo.GetAllLibrariesAsync(stoppingToken);

            foreach(var lib in libraries)

            {
                var chunks = await chunkRepo.GetChunksAsync(lib.Id, lib.CurrentVersion, stoppingToken);

                var embeddedChunks = chunks.Where(c => c.Embedding != null).ToList();

                if (embeddedChunks.Count > 0)

                {
                    await vectorSearch.IndexChunksAsync(profile,
                                                        lib.Id,
                                                        lib.CurrentVersion,
                                                        embeddedChunks,
                                                        stoppingToken
                                                       );

                    mLogger.LogInformation("Loaded {Count} chunks for {Profile}/{Library} v{Version}",
                                           embeddedChunks.Count,
                                           profile ?? "(default)",
                                           lib.Id,
                                           lib.CurrentVersion
                                          );
                }
            }
        }

        catch(Exception ex) when(ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)

        {
            mLogger.LogWarning(ex, "Failed to load chunks for profile {Profile}, skipping", profile ?? "(default)");
        }
    }


    private static IReadOnlyList<string?> GetProfilesToBootstrap(SaddleRagDbContextFactory contextFactory,
                                                                 SaddleRagDbSettings dbSettings)

    {
        var result = new List<string?> { null };


        if (dbSettings.BootstrapAllProfilesAtStartup)

            result.AddRange(contextFactory.GetProfileNames().Cast<string?>());

        else

        {
            var defaultProfileName = contextFactory.GetDefaultProfileName();

            if (!string.IsNullOrWhiteSpace(defaultProfileName))

                result.Add(defaultProfileName);
        }


        var distinctProfiles = result
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToList();


        return distinctProfiles;
    }


    /// <summary>
    ///     Decision predicate the outer warmup catch uses to detect that
    ///     an inner catch already recorded a more-specific failure phase
    ///     (e.g., <see cref="PhaseOnnxDownloadFailed" />). When true, the
    ///     outer catch must NOT call <see cref="McpWarmupState.MarkFailed" />
    ///     again — doing so would overwrite the specific phase string the
    ///     monitor UI surfaces and double-log the same exception. Exposed
    ///     as <c>internal</c> so SaddleRAG.Tests can lock in the contract
    ///     without rebuilding the full warmup dependency graph.
    /// </summary>
    internal static bool HasInnerCatchAlreadyMarkedFailure(McpWarmupState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool result = string.Equals(state.Status, nameof(ScrapeJobStatus.Failed), StringComparison.Ordinal);
        return result;
    }

    /// <summary>
    ///     Drives one rerank inference at startup so the first user
    ///     search doesn't pay the cold-path cost. The ONNX cross-encoder
    ///     session triggers graph optimization, kernel selection, and
    ///     SentencePiece tokenizer initialization on its first
    ///     <c>session.Run</c>; without this probe that overhead lands on
    ///     the first user query (Phase 4 measured ~6 s on CPU for
    ///     mxbai-rerank-base-v1). ONNX Runtime selects per-shape kernels,
    ///     so the probe sends <paramref name="candidateCount" /> chunks
    ///     of long content — matching production's typical
    ///     <c>RankingSettings.MaxReRankCandidates × ~MaxSequenceLength</c>
    ///     shape — rather than one tiny pair, which would warm a kernel
    ///     production never hits. When the active reranker entry resolves
    ///     to null (Onnx.ActiveRerankerModel = "none" or
    ///     Onnx.Enabled = false) the pass-through path executes and the
    ///     probe is effectively a no-op. Exposed as <c>internal</c> so
    ///     SaddleRAG.Tests can invoke it with a fake
    ///     <see cref="IReRanker" /> without rebuilding the full warmup
    ///     dependency graph.
    /// </summary>
    internal static async Task WarmReRankerAsync(IReRanker reRanker, int candidateCount, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reRanker);

        int probeCount = Math.Max(val1: 1, candidateCount);
        var probeChunks = new DocChunk[probeCount];
        for(var i = 0; i < probeCount; i++)
            probeChunks[i] = BuildWarmupRerankProbeChunk(i);

        await reRanker.ReRankAsync(WarmupRerankProbeQuery,
                                   probeChunks,
                                   probeCount,
                                   ct
                                  );
    }

    private static DocChunk BuildWarmupRerankProbeChunk(int index)
    {
        return new DocChunk
                   {
                       Id = $"{WarmupRerankProbeChunkId}-{index}",
                       LibraryId = WarmupRerankProbeLibraryId,
                       Version = WarmupRerankProbeVersion,
                       PageUrl = WarmupRerankProbePageUrl,
                       PageTitle = WarmupRerankProbePageTitle,
                       Category = DocCategory.Sample,
                       Content = smWarmupRerankProbeLongContent
                   };
    }


    private const string PhaseStarting = "Starting";

    private const string PhaseMongoDbProfilesDiscovered = "MongoDB profiles discovered";

    private const string PhaseOllamaBootstrapFinished = "Ollama bootstrap finished";

    private const string PhaseOnnxModelsReady = "ONNX models ready";

    private const string PhaseOnnxDownloadFailed = "ONNX download failed";

    private const string PhaseVectorIndicesLoaded = "Vector indices loaded";

    private const string PhaseCanceled = "Canceled";

    private const string OnnxDownloadFailedLogTemplate = "[Warmup] T+{Sec:F1}s - ONNX model download failed; embedding/reranking will not be available until the operator resolves this. Check network, Onnx.ModelsDir permissions, and the configured RepoId/ModelFile values.";

    private const string WarmupProbeText = "warmup";

    private const string WarmupSearchProbeText = "warmup search";

    private const string WarmupCanceledMessage = "Warmup canceled during shutdown.";

    private const string WarmupRerankProbeQuery = "warmup rerank probe";

    private const string WarmupRerankProbeChunkId = "warmup-rerank-probe";

    private const string WarmupRerankProbeLibraryId = "warmup";

    private const string WarmupRerankProbeVersion = "warmup";

    private const string WarmupRerankProbePageUrl = "https://warmup.local";

    private const string WarmupRerankProbePageTitle = "warmup";

    /// <summary>
    ///     Base sentence repeated <see cref="WarmupRerankProbeRepeatCount" />
    ///     times to build <see cref="smWarmupRerankProbeLongContent" />.
    ///     The content itself doesn't matter for warmup quality —
    ///     only the post-tokenization length does. SentencePiece on
    ///     English averages ~4-5 chars/token, so ~100 chars × ~30
    ///     repetitions ≈ 600+ tokens reliably hits
    ///     <c>MaxSequenceLength=512</c> after
    ///     <c>OnnxReRanker.BuildPair</c> clamps.
    /// </summary>
    private const string WarmupRerankProbeSentence =
        "Math.NET Numerics provides distributions linear algebra decompositions iterative solvers optimization statistics. ";

    /// <summary>
    ///     Number of <see cref="WarmupRerankProbeSentence" /> copies that
    ///     compose the long warmup-probe doc content.
    /// </summary>
    private const int WarmupRerankProbeRepeatCount = 30;

    /// <summary>
    ///     Synthetic doc content sized to fill the cross-encoder's
    ///     <c>MaxSequenceLength</c> after tokenization, so the warmup
    ///     session.Run shape <c>[MaxReRankCandidates, ~512]</c> matches
    ///     what production queries hit. A shorter probe would warm a
    ///     different ORT kernel and leave the production shape cold.
    /// </summary>
    private static readonly string smWarmupRerankProbeLongContent =
        string.Concat(Enumerable.Repeat(WarmupRerankProbeSentence, WarmupRerankProbeRepeatCount));

    private const string RerankerPassThroughLabel = "pass-through";
}
