// Program.cs

// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.


#region Usings

using System.Diagnostics;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Hosting.WindowsServices;
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
using Serilog;
using Serilog.Core;
using Serilog.Events;

#endregion


const string AppName = "SaddleRAG";
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

builder.Host.UseSerilog();
builder.Host.UseWindowsService(options => { options.ServiceName = ServiceName; });
builder.Services.AddSingleton(levelSwitch);

builder.Services.AddSingleton(new DiagnosticTools.LogConfig(logDirectory));

builder.Services.AddSingleton<McpWarmupState>();

builder.Services.AddHostedService<McpWarmupService>();


// MongoDB

builder.Services.AddSaddleRagDatabase(builder.Configuration);


// Ollama configuration

builder.Services.Configure<OllamaSettings>(builder.Configuration.GetSection(OllamaSettings.SectionName));

// Ranking configuration (BM25 weight, ReRank blend weight, ProseMentionThreshold, ReRankerStrategy)
builder.Services.Configure<RankingSettings>(builder.Configuration.GetSection(RankingSettings.SectionName));


// Ollama services

builder.Services.AddSingleton<OllamaBootstrapper>();

builder.Services.AddSingleton<IEmbeddingProvider, OllamaEmbeddingProvider>();

builder.Services.AddSingleton<IVectorSearchProvider, InMemoryBruteForceVectorSearch>();

// Re-ranker (toggleable at runtime via MCP tool)
builder.Services.AddSingleton<ToggleableReRanker>();
builder.Services.AddSingleton<IReRanker>(sp => sp.GetRequiredService<ToggleableReRanker>());


// Classification

builder.Services.AddSingleton<LlmClassifier>();

// Recon flow (LibraryProfile validation/persistence + CLI Ollama fallback)
builder.Services.AddSingleton<LibraryProfileService>();
builder.Services.AddSingleton<CliReconFallback>();

// Identifier-aware extractor (consumed by CategoryAwareChunker and rescrub_library)
builder.Services.AddSingleton<SymbolExtractor>();

// Rescrub service and background job runner (consumed by rescrub_library MCP tool)
builder.Services.AddSingleton<RescrubService>();
builder.Services.AddSingleton<RescrubJobRunner>();
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
       .WithToolsFromAssembly();

// Blazor Server + SignalR for /monitor
builder.Services.AddRazorComponents()
       .AddInteractiveServerComponents();
builder.Services.AddSignalR();
builder.Services.AddMudServices();
builder.Services.AddHostedService<MonitorTickService>();
builder.Services.AddHostedService<MonitorLifecycleRelay>();
builder.Services.AddSingleton<MonitorDataService>();
builder.Services.AddSingleton<MonitorJobService>();

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

    using var prewarmCts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds: 60));

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
    MonitorSnapshotEndpoints.Map(app);


    app.Run();
}

Log.CloseAndFlush();

return exitCode;
