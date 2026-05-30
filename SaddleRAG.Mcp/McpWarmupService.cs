// McpWarmupService.cs

// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Diagnostics;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Database.Migrations;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Classification;
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

            var onnxSettings = scope.ServiceProvider.GetRequiredService<IOptions<OnnxSettings>>().Value;

            var classifier = scope.ServiceProvider.GetRequiredService<ILlmClassifier>();

            var vectorSearch = scope.ServiceProvider.GetRequiredService<IVectorSearchProvider>();

            // Whether any Ollama backend is actually active. The Ollama
            // bootstrap (model pulls + keep-alive warm) only runs when at
            // least one of these is true:
            //   - Ollama provides embeddings (Onnx disabled, or ONNX on but
            //     EmbeddingEnabled=false — the embedding fallback path), OR
            //   - the active classifier backend is Ollama.
            // On the all-ONNX path (ONNX embed + ONNX classifier) the
            // bootstrap/warm is skipped entirely, removing the slow/failing
            // Ollama classification warm from the default startup.
            bool ollamaEmbeddingActive = !onnxSettings.Enabled || !onnxSettings.EmbeddingEnabled;
            bool ollamaClassifierActive = classifier is ClassifierBackendSwitch backendSwitch
                                          && string.Equals(backendSwitch.ActiveBackendName,
                                                           OllamaBackendName,
                                                           StringComparison.OrdinalIgnoreCase
                                                          );
            bool ollamaBackendActive = ollamaEmbeddingActive || ollamaClassifierActive;

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

            if (ollamaBackendActive)
            {
                await bootstrapper.BootstrapAsync(requiredModels.ToList(), stoppingToken);

                mWarmupState.MarkPhase(PhaseOllamaBootstrapFinished);

                mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - Ollama bootstrap finished",
                                       startupSw.Elapsed.TotalSeconds,
                                       stepSw.ElapsedMilliseconds
                                      );
            }
            else
            {
                mWarmupState.MarkPhase(PhaseOllamaBootstrapSkipped);

                mLogger.LogInformation("[Warmup] T+{Sec:F1}s - Ollama bootstrap skipped (all-ONNX path)",
                                       startupSw.Elapsed.TotalSeconds
                                      );
            }


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


            // Download the active classifier model folder (idempotent) and
            // warm the ONNX classifier so the first real classification isn't
            // cold. Gated on Onnx.Enabled — when ONNX is off the classifier
            // path is Ollama and this whole block is skipped. Non-fatal: a
            // download or warm failure logs a warning but does NOT abort
            // warmup (mirrors the search-path warm's non-fatal pattern), so
            // the operator can still serve search while resolving the issue.
            if (onnxSettings.Enabled)
            {
                stepSw.Restart();

                await EnsureAndWarmOnnxClassifierAsync(onnxDownloader,
                                                       onnxSettings,
                                                       classifier,
                                                       startupSw,
                                                       stepSw,
                                                       stoppingToken
                                                      );
            }


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


            // Classifier (Ollama generate) warm runs LAST and is best-effort: it must
            // never abort the ONNX embed/rerank warm above, so the search path stays
            // warm regardless. A failed/slow classifier warm only means the first
            // reextract_library / dryrun_scrape call cold-loads the model; interactive
            // search is unaffected. Surfaced via a WARNING + the completed phase note,
            // not a Failed warmup state.
            bool classifierWarm = false;

            stepSw.Restart();

            try

            {
                await bootstrapper.WarmModelsAsync(stoppingToken);

                classifierWarm = true;

                mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - generate (classifier) models warm",
                                       startupSw.Elapsed.TotalSeconds,
                                       stepSw.ElapsedMilliseconds
                                      );
            }

            catch(Exception ex) when(ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)

            {
                mLogger.LogWarning(ex, "[Warmup] T+{Sec:F1}s - Generate (classifier) model warm failed; search path is warm, classification will cold-load on first use", startupSw.Elapsed.TotalSeconds);
            }


            mWarmupState.MarkCompleted(classifierWarm ? nameof(ScrapeJobStatus.Completed) : PhaseClassifierDegraded);

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


    private const string PhaseClassifierDegraded = "Completed (classifier warm failed; search ready)";

    /// <summary>
    ///     Downloads the active classifier model folder (idempotent) and warms
    ///     the ONNX classifier by classifying a tiny probe page, which triggers
    ///     the generator's lazy GenAI-model load. Non-fatal: any failure logs a
    ///     warning and marks <see cref="PhaseOnnxClassifierWarmSkipped" /> so
    ///     warmup continues — search is unaffected and the first real
    ///     classification will simply pay the cold-load cost. Resolves the
    ///     active classifier variant against the configured execution provider
    ///     so the directml/cuda/cpu folder downloaded matches the generator's.
    /// </summary>
    private async Task EnsureAndWarmOnnxClassifierAsync(OnnxModelDownloader downloader,
                                                        OnnxSettings onnxSettings,
                                                        ILlmClassifier classifier,
                                                        Stopwatch startupSw,
                                                        Stopwatch stepSw,
                                                        CancellationToken ct)
    {
        try
        {
            var entry = ClassifierEntryResolver.Resolve(onnxSettings, onnxSettings.ExecutionProvider);
            string targetDir = Path.Combine(onnxSettings.ModelsDir, entry.Name);

            await downloader.DownloadModelFolderAsync(entry.RepoId, entry.ModelFolder, targetDir, ct);

            mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - ONNX classifier model ready ({Entry})",
                                   startupSw.Elapsed.TotalSeconds,
                                   stepSw.ElapsedMilliseconds,
                                   entry.Name
                                  );

            stepSw.Restart();

            await classifier.ClassifyAsync(BuildClassifierWarmupProbePage(), ClassifierWarmupLibraryHint, ct);

            mWarmupState.MarkPhase(PhaseOnnxClassifierWarm);

            mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - ONNX classifier warm",
                                   startupSw.Elapsed.TotalSeconds,
                                   stepSw.ElapsedMilliseconds
                                  );
        }
        catch(Exception ex) when(ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            mWarmupState.MarkPhase(PhaseOnnxClassifierWarmSkipped);

            mLogger.LogWarning(ex,
                               "[Warmup] T+{Sec:F1}s - ONNX classifier download/warm failed (non-fatal); the first classification will cold-load.",
                               startupSw.Elapsed.TotalSeconds
                              );
        }
    }


    private static PageRecord BuildClassifierWarmupProbePage()
    {
        return new PageRecord
                   {
                       Id = ClassifierWarmupProbeId,
                       LibraryId = ClassifierWarmupLibraryHint,
                       Version = ClassifierWarmupProbeVersion,
                       Url = ClassifierWarmupProbeUrl,
                       Title = ClassifierWarmupProbeTitle,
                       Category = DocCategory.Unclassified,
                       RawContent = ClassifierWarmupProbeContent,
                       FetchedAt = DateTime.UtcNow,
                       ContentHash = ClassifierWarmupProbeHash
                   };
    }


    private const string PhaseStarting = "Starting";

    private const string PhaseMongoDbProfilesDiscovered = "MongoDB profiles discovered";

    private const string PhaseOllamaBootstrapFinished = "Ollama bootstrap finished";

    private const string PhaseOnnxModelsReady = "ONNX models ready";

    private const string PhaseOllamaBootstrapSkipped = "Ollama bootstrap skipped (all-ONNX path)";

    private const string PhaseOnnxClassifierWarm = "ONNX classifier warm";

    private const string PhaseOnnxClassifierWarmSkipped = "ONNX classifier warm skipped";

    private const string PhaseOnnxDownloadFailed = "ONNX download failed";

    private const string PhaseVectorIndicesLoaded = "Vector indices loaded";

    private const string PhaseCanceled = "Canceled";

    private const string OnnxDownloadFailedLogTemplate = "[Warmup] T+{Sec:F1}s - ONNX model download failed; embedding/reranking will not be available until the operator resolves this. Check network, Onnx.ModelsDir permissions, and the configured RepoId/ModelFile values.";

    private const string WarmupProbeText = "warmup";

    private const string WarmupSearchProbeText = "warmup search";

    private const string OllamaBackendName = "ollama";

    private const string ClassifierWarmupLibraryHint = "warmup";

    private const string ClassifierWarmupProbeId = "warmup-classifier-probe";

    private const string ClassifierWarmupProbeVersion = "warmup";

    private const string ClassifierWarmupProbeUrl = "https://warmup.local/classify";

    private const string ClassifierWarmupProbeTitle = "warmup";

    private const string ClassifierWarmupProbeContent = "Warmup probe page for the ONNX classifier. This text is classified once at startup so the GenAI model loads off the hot path.";

    private const string ClassifierWarmupProbeHash = "warmup";

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
