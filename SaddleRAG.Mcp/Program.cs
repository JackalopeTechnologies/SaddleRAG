// Program.cs

// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Diagnostics;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using MudBlazor.Services;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Chunking;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Crawling;
using SaddleRAG.Ingestion.Diagnostics;
using SaddleRAG.Ingestion.Ecosystems.Common;
using SaddleRAG.Ingestion.Ecosystems.Npm;
using SaddleRAG.Ingestion.Ecosystems.NuGet;
using SaddleRAG.Ingestion.Ecosystems.Pip;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Recon;
using SaddleRAG.Ingestion.Scanning;
using SaddleRAG.Ingestion.Suspect;
using SaddleRAG.Ingestion.Symbols;
using SaddleRAG.Mcp;
using SaddleRAG.Mcp.Api;
using SaddleRAG.Mcp.Auth;
using SaddleRAG.Mcp.Hubs;
using SaddleRAG.Mcp.Monitor;
using SaddleRAG.Mcp.Tools;
using SaddleRAG.Monitor.Pages;
using SaddleRAG.Monitor.Services;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Packaging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

#endregion


const string AppName = "SaddleRAG";
const string McpApplicationName = "SaddleRAG.Mcp";
const string DataProtectionKeysSubdirectory = "DataProtection-Keys";
const string LogSubdirectory = "logs";
const string MicrosoftAspNetCoreNamespace = "Microsoft.AspNetCore";
const string LogFileNamePattern = "saddlerag-.log";
const string HttpClientNuGet = "NuGet";
const string HttpClientNpm = "npm";
const string HttpClientPyPi = "PyPI";
const string HttpClientDocUrlProbe = "DocUrlProbe";
const string KestrelHttpsEndpointKey = "Kestrel:Endpoints:Https:Url";
const string HealthEndpointPath = "/health";
const string HealthyStatus = "Healthy";
const string MonitorHubEndpointPath = "/monitor/hub";
const string MonitorEndpointPath = "/monitor";
const string KestrelHttpPortKey = "Kestrel:Endpoints:Http:Port";
const int DefaultMonitorPort = 6100;

var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                AppName,
                                LogSubdirectory
                               );

Directory.CreateDirectory(logDirectory);


var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Warning);

const string McpEndpointPattern = "/mcp";

const string ServiceName = "SaddleRAGMcp";

const string EventLogName = "Application";

var loggerConfig = new LoggerConfiguration()
                   .MinimumLevel.ControlledBy(levelSwitch)
                   .MinimumLevel.Override(MicrosoftAspNetCoreNamespace, LogEventLevel.Warning)
                   .WriteTo.Console()
                   .WriteTo.File(Path.Combine(logDirectory, LogFileNamePattern),
                                 rollingInterval: RollingInterval.Day,
                                 retainedFileCountLimit: 7,
                                 shared: true
                                );

if (WindowsServiceHelpers.IsWindowsService())

{
    loggerConfig = loggerConfig.WriteTo.EventLog(ServiceName,
                                                 EventLogName,
                                                 manageEventSource: true,
                                                 restrictedToMinimumLevel: LogEventLevel.Warning
                                                );
}

Log.Logger = loggerConfig.CreateLogger();


var builder = WebApplication.CreateBuilder(args);

// Runtime overrides file: written by the set_active_embedding_model /
// set_active_reranker_model / set_execution_provider MCP tools via
// OnnxOverrideStore. Registered after appsettings.json so its values
// take precedence on next process start. Optional + reloadOnChange so a
// missing file is fine and edits are picked up without restart by any
// IOptionsMonitor consumers (the in-process InferenceSession still
// requires a restart, hence the RequiresRestart flag the tools return).
builder.Configuration.AddJsonFile(OnnxOverrideStore.RuntimeOverridesFileName,
                                  optional: true, reloadOnChange: true
                                 );

builder.Host.UseSerilog();
if (OperatingSystem.IsWindows())
    builder.Host.UseWindowsService(options => { options.ServiceName = ServiceName; });

if (builder.Environment.IsDevelopment())
{
    var dataProtectionDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                               AppName,
                                               DataProtectionKeysSubdirectory,
                                               builder.Environment.EnvironmentName
                                              );

    Directory.CreateDirectory(dataProtectionDirectory);

    var dataProtectionBuilder = builder.Services.AddDataProtection()
                                       .SetApplicationName($"{McpApplicationName}.{builder.Environment.EnvironmentName}")
                                       .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory));

    if (OperatingSystem.IsWindows())
        dataProtectionBuilder.ProtectKeysWithDpapi();
}

builder.Services.AddSingleton(levelSwitch);

builder.Services.AddSingleton(new DiagnosticTools.LogConfig(logDirectory));

builder.Services.AddSingleton<McpWarmupState>();

builder.Services.AddHostedService<McpWarmupService>();


// MongoDB

builder.Services.AddSaddleRagDatabase(builder.Configuration);

// Packaging services — CollectionCompactor is shared between compact_collections MCP tool
// and the import_library compact opt-in. LibraryExporter and LibraryImporter are registered
// here in anticipation of the export_library / import_library MCP tools (Task 22).
builder.Services.AddSingleton<ICollectionCompactor, CollectionCompactor>();
builder.Services.AddSingleton<LibraryExporter>();
builder.Services.AddSingleton<LibraryImporter>(sp =>
{
    var factory = sp.GetRequiredService<RepositoryFactory>();
    return new LibraryImporter(
        sp.GetRequiredService<ILibraryRepository>(),
        sp.GetRequiredService<IJobRepository>(),
        sp.GetRequiredService<IEmbeddingProvider>(),
        sp.GetRequiredService<ILibraryProfileRepository>(),
        sp.GetRequiredService<ILibraryIndexRepository>(),
        sp.GetRequiredService<IExcludedSymbolsRepository>(),
        sp.GetRequiredService<IDiffRepository>(),
        sp.GetRequiredService<IPageRepository>(),
        sp.GetRequiredService<IChunkRepository>(),
        sp.GetRequiredService<IBm25ShardRepository>(),
        sp.GetRequiredService<ICollectionCompactor>(),
        profile => factory.GetDatabase(profile));
});

// Ollama configuration

builder.Services.Configure<OllamaSettings>(builder.Configuration.GetSection(OllamaSettings.SectionName));

// ONNX configuration (in-process embedding + reranking via Microsoft.ML.OnnxRuntime).
// When Onnx.Enabled && Onnx.EmbeddingEnabled, the OnnxEmbeddingProvider is
// registered as IEmbeddingProvider; otherwise the OllamaEmbeddingProvider
// stays as the active embedder. ToggleableReRanker reads
// RankingSettings.ReRankerStrategy per-call to decide whether to dispatch
// to OnnxReRanker, the legacy Ollama rerankers, or pass-through.
// Bind + validate Onnx settings via the AddOptions pipeline. ValidateOnStart()
// runs OnnxSettingsValidator during IHost.StartAsync (before the HTTP server
// accepts requests), so any registry misconfig — duplicate names, unknown
// ActiveEmbeddingModel, Bert entry missing VocabFile, etc. — fails the host
// with an OptionsValidationException listing every failure at once. Without
// ValidateOnStart() the validator would fire lazily on the first
// IOptions<OnnxSettings>.Value access deep inside the warmup background
// thread, hitting the warmup catch path with a generic error and never
// reaching the operator at the canonical startup-failure surface.
builder.Services.AddOptions<OnnxSettings>()
       .Bind(builder.Configuration.GetSection(OnnxSettings.SectionName))
       .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<OnnxSettings>, OnnxSettingsValidator>();

// Tracks which OnnxRuntime execution providers are compiled into this build
// flavor (USE_GPU symbol) and which one the running embedding / reranker
// sessions actually loaded with. The list_execution_providers MCP tool reads
// it so the LLM can see whether a requested GPU EP took effect or fell back.
builder.Services.AddSingleton<OnnxRuntimeCapabilities>();

// Persists set_active_embedding_model / set_active_reranker_model /
// set_execution_provider mutations to runtime-overrides.json so they survive
// restart. Mutates the live OnnxSettings singleton for in-process visibility.
builder.Services.AddSingleton<OnnxOverrideStore>();

// Ranking configuration (BM25 weight, ReRank blend weight, ProseMentionThreshold, ReRankerStrategy)
builder.Services.Configure<RankingSettings>(builder.Configuration.GetSection(RankingSettings.SectionName));


// Ollama services

builder.Services.AddSingleton<OllamaBootstrapper>();

// Embedding provider — switch between ONNX and Ollama based on Onnx.Enabled+EmbeddingEnabled.
var onnxSettingsForDi = builder.Configuration.GetSection(OnnxSettings.SectionName).Get<OnnxSettings>()
                        ?? new OnnxSettings();
if (onnxSettingsForDi is { Enabled: true, EmbeddingEnabled: true })
    builder.Services.AddSingleton<IEmbeddingProvider, OnnxEmbeddingProvider>();
else
    builder.Services.AddSingleton<IEmbeddingProvider, OllamaEmbeddingProvider>();

// Onnx model downloader (HuggingFace fetches into Onnx.ModelsDir during warmup).
builder.Services.AddHttpClient(OnnxModelDownloader.HttpClientName);
builder.Services.AddSingleton<OnnxModelDownloader>();

builder.Services.AddSingleton<IVectorSearchProvider, InMemoryBruteForceVectorSearch>();

// Re-ranker (toggleable at runtime via MCP tool). OnnxReRanker is registered
// regardless of Onnx.Enabled because ToggleableReRanker holds a reference
// to it; if Onnx.ActiveRerankerModel resolves to null at construction the
// reranker behaves as pass-through.
builder.Services.AddSingleton<OnnxReRanker>();
builder.Services.AddSingleton<ToggleableReRanker>();
builder.Services.AddSingleton<IReRanker>(sp => sp.GetRequiredService<ToggleableReRanker>());


// Classification

builder.Services.AddSingleton<OllamaLlmClassifier>();

// Recon flow (LibraryProfile validation/persistence + CLI Ollama fallback)
builder.Services.AddSingleton<LibraryProfileService>();
builder.Services.AddSingleton<CliReconFallback>();

// Identifier-aware extractor (consumed by CategoryAwareChunker and reextract_library)
builder.Services.AddSingleton<SymbolExtractor>();

// Reextract service and background job runner (consumed by reextract_library MCP tool)
builder.Services.AddSingleton<RescrubService>();
builder.Services.AddSingleton<RescrubJobRunner>();

// Shared cancellation registry — every cancellable job runner registers its
// per-job CTS here so cancel_job (and the monitor API) can signal it without
// knowing which runner owns the job.
builder.Services.AddSingleton<IJobCancellationRegistry, JobCancellationRegistry>();
builder.Services.AddSingleton<JobCancellationService>();

// Reembed service and background job runner (consumed by reembed_library MCP tool)
builder.Services.AddSingleton<ReembedService>();
builder.Services.AddSingleton<ReembedJobRunner>();

builder.Services.AddSingleton<BackgroundJobRunner>();
builder.Services.AddSingleton<IBackgroundJobRunner>(sp =>
                                                        sp.GetRequiredService<BackgroundJobRunner>()
                                                   );
builder.Services.AddKeyedSingleton<IBackgroundJobRunner>(nameof(IBackgroundJobRunner),
                                                         (sp, _) => sp.GetRequiredService<BackgroundJobRunner>()
                                                        );

// Rechunk service (consumed by rechunk_library MCP tool)
builder.Services.AddSingleton<RechunkService>();


// Ingestion pipeline (so MCP can scrape on demand)

builder.Services.AddSingleton<GitHubRepoScraper>();

builder.Services.AddSingleton<PageCrawler>();
builder.Services.AddSingleton<IPageCrawler>(sp => sp.GetRequiredService<PageCrawler>());

builder.Services.AddSingleton<CategoryAwareChunker>();

builder.Services.AddSingleton<SuspectDetector>();

builder.Services.AddSingleton<IngestionOrchestrator>();

builder.Services.AddSingleton<ScrapeJobRunner>();

builder.Services.AddSingleton<IScrapeJobQueue>(sp =>
                                                   sp.GetRequiredService<ScrapeJobRunner>()
                                              );

builder.Services.AddSingleton<IScrapeAuditWriter>(sp =>
                                                      new ScrapeAuditWriter(sp.GetRequiredService<
                                                                                IScrapeAuditRepository>()
                                                                           )
                                                 );

builder.Services.AddSingleton<MonitorBroadcaster>();
builder.Services.AddSingleton<IMonitorBroadcaster>(sp =>
                                                       sp.GetRequiredService<MonitorBroadcaster>()
                                                  );
builder.Services.AddSingleton<IMonitorEvents>(sp =>
                                                  sp.GetRequiredService<MonitorBroadcaster>()
                                             );

builder.Services.AddSingleton<QueryMetricsRecorder>();
builder.Services.AddSingleton<IQueryMetrics>(sp =>
                                                 sp.GetRequiredService<QueryMetricsRecorder>()
                                            );


// HTTP clients for package registry APIs

builder.Services.AddHttpClient(HttpClientNuGet)
       .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler

                                                     {
                                                         AutomaticDecompression = DecompressionMethods.All
                                                     }
                                          );

builder.Services.AddHttpClient(HttpClientNpm);

builder.Services.AddHttpClient(HttpClientPyPi);

builder.Services.AddHttpClient(HttpClientDocUrlProbe);


// Shared utilities

builder.Services.AddSingleton<CommonDocUrlPatterns>();

builder.Services.AddSingleton<PackageFilter>();


// NuGet ecosystem

builder.Services.AddSingleton<IProjectFileParser, NuGetProjectFileParser>();

builder.Services.AddSingleton<IPackageRegistryClient, NuGetRegistryClient>();

builder.Services.AddSingleton<IDocUrlResolver, NuGetDocUrlResolver>();


// npm ecosystem

builder.Services.AddSingleton<IProjectFileParser, NpmProjectFileParser>();

builder.Services.AddSingleton<IPackageRegistryClient, NpmRegistryClient>();

builder.Services.AddSingleton<IDocUrlResolver, NpmDocUrlResolver>();


// pip ecosystem

builder.Services.AddSingleton<IProjectFileParser, PipProjectFileParser>();

builder.Services.AddSingleton<IPackageRegistryClient, PyPiRegistryClient>();

builder.Services.AddSingleton<IDocUrlResolver, PipDocUrlResolver>();


// Dependency indexing orchestrator

builder.Services.AddSingleton<DependencyIndexer>();


// MCP server version — sourced from AssemblyInformationalVersion which the
// build workflow stamps from the git tag (e.g. "0.1.0-alpha.3"). Local dev
// builds get the Directory.Build.props default ("0.0.0-dev") so clients can
// tell they're not running a tagged release.
const string DefaultDevVersion = "0.0.0-dev";
var serverVersion = Assembly.GetExecutingAssembly()
                            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                            ?.InformationalVersion ??
                    DefaultDevVersion;

// MCP server with Streamable HTTP transport

builder.Services
       .AddMcpServer(options =>
                     {
                         options.ServerInfo = new Implementation

                                                  {
                                                      Name = "SaddleRAG â€” Documentation RAG MCP Server",

                                                      Version = serverVersion
                                                  };
                     }
                    )
       .WithHttpTransport(t => t.Stateless = true)
       .UseToolExceptionFilter()
       .WithListResourcesHandler(static (request, cancellationToken) =>
                                     ValueTask.FromResult(new ListResourcesResult { Resources = [] }))
       .WithListResourceTemplatesHandler(static (request, cancellationToken) =>
                                             ValueTask.FromResult(new ListResourceTemplatesResult { ResourceTemplates = [] }))
       .WithToolsFromAssembly();

// Blazor Server + SignalR for /monitor
builder.Services.AddRazorComponents()
       .AddInteractiveServerComponents();
// Default DisconnectedCircuitRetentionPeriod is 3 minutes — too short for the
// monitor pages which operators leave open overnight. After expiry the server
// purges the circuit and reconnects fail; without the reconnect modal in
// App.razor that would silently surface as stale data.
builder.Services.Configure<CircuitOptions>(options =>
                                           {
                                               options.DisconnectedCircuitRetentionPeriod =
                                                   TimeSpan.FromHours(value: 8);
                                           }
                                          );
builder.Services.AddSignalR();
builder.Services.AddMudServices();
builder.Services.AddHostedService<MonitorTickService>();
builder.Services.AddHostedService<MonitorLifecycleRelay>();
builder.Services.AddSingleton<IUnifiedJobView, UnifiedJobView>();
builder.Services.AddSingleton<MonitorDataService>();
builder.Services.AddSingleton<MonitorJobService>();
builder.Services.AddSingleton<IMonitorConfigSource, McpMonitorConfigSource>();

var monitorPort = builder.Configuration.GetValue<int?>(KestrelHttpPortKey) ?? DefaultMonitorPort;
builder.Services.AddHttpClient<MonitorWriteService>(client =>
                                                        client.BaseAddress = new Uri($"http://localhost:{monitorPort}/")
                                                   );

builder.Services.AddAuthorization(opts =>
                                      opts.AddPolicy(DiagnosticsWriteRequirement.PolicyName,
                                                     policy => policy.AddRequirements(new DiagnosticsWriteRequirement())
                                                    )
                                 );
builder.Services.AddSingleton<IAuthorizationHandler,
    DiagnosticsWriteHandler>();


const string PrewarmFlag = "--prewarm";

var startupSw = Stopwatch.StartNew();

var app = builder.Build();

bool prewarmMode = args.Any(a => string.Equals(a, PrewarmFlag, StringComparison.OrdinalIgnoreCase));

var exitCode = 0;

if (prewarmMode)

{
    app.Logger.LogWarning("[Prewarm] Host built in {Sec:F1}s, starting full host to warm cache",
                          startupSw.Elapsed.TotalSeconds
                         );

    // Scale prewarm timeout when ONNX is enabled: first install downloads
    // ~364 MB (nomic-fp16 + mxbai-base quantized). 60s is fine for warm
    // re-runs; cold first-install needs more.
    const int PrewarmBaseTimeoutSeconds = 60;
    const int PrewarmOnnxOverheadSeconds = 600;
    int prewarmSeconds = onnxSettingsForDi.Enabled
                             ? PrewarmBaseTimeoutSeconds + PrewarmOnnxOverheadSeconds
                             : PrewarmBaseTimeoutSeconds;
    using var prewarmCts = new CancellationTokenSource(TimeSpan.FromSeconds(prewarmSeconds));

    try

    {
        await app.StartAsync(prewarmCts.Token);

        await app.StopAsync(prewarmCts.Token);

        app.Logger.LogWarning("[Prewarm] Host start+stop complete in {Sec:F1}s, reading install dir to warm OS file cache",
                              startupSw.Elapsed.TotalSeconds
                             );

        (var fileCount, var totalBytes) = PrewarmHelpers.ReadAllFiles(AppContext.BaseDirectory);

        app.Logger.LogWarning("[Prewarm] Read {Count} files ({MB:F0} MB) in {Sec:F1}s total",
                              fileCount,
                              totalBytes / (double) PrewarmHelpers.BytesPerMegabyte,
                              startupSw.Elapsed.TotalSeconds
                             );
    }

    catch(Exception ex)

    {
        app.Logger.LogError(ex, "[Prewarm] Failed during host start/stop");

        if (StartupFailureReporter.IsPortBindFailure(ex))
            StartupFailureReporter.WriteBanner(StartupFailureReporter.ExtractPort(ex, monitorPort));

        exitCode = 1;
    }
}

else

{
    app.Logger.LogInformation("[Startup] T+{Sec:F1}s â€” HTTP server starting", startupSw.Elapsed.TotalSeconds);


    // Log first real request

    var firstRequestLogged = false;

    app.Use(async (context, next) =>

            {
                if (!firstRequestLogged)

                {
                    firstRequestLogged = true;

                    app.Logger.LogInformation("[Startup] T+{Sec:F1}s â€” First request: {Method} {Path}",
                                              startupSw.Elapsed.TotalSeconds,
                                              context.Request.Method,
                                              context.Request.Path
                                             );
                }


                await next();
            }
           );


    // HTTPS redirection â€” enabled only when an HTTPS endpoint is configured

    var httpsEndpointUrl = app.Configuration[KestrelHttpsEndpointKey];

    if (!string.IsNullOrWhiteSpace(httpsEndpointUrl))

        app.UseHttpsRedirection();

    // Static files for Blazor framework script and Razor Class Library assets (e.g. MudBlazor)
    app.UseStaticFiles();


    // Health check

    app.MapGet(HealthEndpointPath,
               (McpWarmupState warmupState) => Results.Ok(new

                                                              {
                                                                  Status = HealthyStatus,

                                                                  WarmupStatus = warmupState.Status,

                                                                  WarmupPhase = warmupState.CurrentPhase,

                                                                  WarmupError = warmupState.LastError
                                                              }
                                                         )
              );


    // Root redirect → monitor UI
    app.MapGet("/", () => Results.Redirect(MonitorEndpointPath));

    // MCP endpoint

    app.MapMcp(McpEndpointPattern);

    // Blazor Server monitor
    app.MapRazorComponents<App>()
       .AddInteractiveServerRenderMode()
       .AddAdditionalAssemblies(typeof(LandingPage).Assembly);

    // SignalR hub
    app.MapHub<MonitorHub>(MonitorHubEndpointPath);

    // Monitor write API
    app.UseAuthorization();
    app.UseAntiforgery();
    MonitorApiEndpoints.Map(app);
    MonitorLibraryActionsEndpoints.Map(app);
    MonitorSnapshotEndpoints.Map(app);


    try
    {
        app.Run();
    }
    catch(Exception ex) when (StartupFailureReporter.IsPortBindFailure(ex))
    {
        StartupFailureReporter.WriteBanner(StartupFailureReporter.ExtractPort(ex, monitorPort));
        exitCode = 1;
    }
}

Log.CloseAndFlush();

return exitCode;
