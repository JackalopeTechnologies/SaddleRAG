// Program.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.CommandLine;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using SaddleRAG.Cli.Commands;
using SaddleRAG.Cli.Handlers;
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

#endregion


#region String constants

const string AppSettingsFile = "appsettings.json";
const string EnvironmentVariablePrefix = "SADDLERAG_";
const string NuGetClientName = "NuGet";
const string NpmClientName = "npm";
const string PyPiClientName = "PyPI";
const string DocUrlProbeClientName = "DocUrlProbe";
const string RootCommandDescription = "SaddleRAG — Documentation Ingestion CLI";
const string IngestCommandName = "ingest";
const string IngestCommandDescription = "Ingest a documentation library";
const string RootUrlOptionName = "--root-url";
const string RootUrlOptionDescription = "Root URL to crawl";
const string LibraryIdOptionName = "--library-id";
const string UniqueLibraryIdDescription = "Unique library identifier";
const string VersionOptionName = "--version";
const string VersionStringDescription = "Version string";
const string HintOptionName = "--hint";
const string HintOptionDescription = "Library description hint";
const string AllowedOptionName = "--allowed";
const string AllowedOptionDescription = "Allowed URL patterns (regex)";
const string ExcludedOptionName = "--excluded";
const string ExcludedOptionDescription = "Excluded URL patterns (regex)";
const string MaxPagesOptionName = "--max-pages";
const string MaxPagesOptionDescription = "Max pages to crawl (0 = unlimited)";
const string DelayOptionName = "--delay";
const string DelayOptionDescription = "Delay between fetches in ms";
const string ListCommandName = "list";
const string ListAllLibrariesDescription = "List all ingested libraries";
const string StatusCommandName = "status";
const string StatusCommandDescription = "Show ingestion status for a library";
const string LibraryIdDescription = "Library identifier";
const string DryrunCommandName = "dryrun";
const string DryrunCommandDescription = "Dry-run a scrape — fetch pages but store nothing";
const string ReclassifyCommandName = "reclassify";
const string ReclassifyCommandDescription =
    "Run the LLM classifier over existing pages still marked Unclassified, without re-scraping. Updates page records and chunk categories in MongoDB.";
const string ReclassifyLibraryIdDescription = "Library to reclassify (omit for all libraries)";
const string AllOptionName = "--all";
const string ReclassifyAllDescription = "Reclassify ALL pages, even ones already classified";
const string InspectCommandName = "inspect";
const string InspectCommandDescription = "Load a single page and report its link/sidebar structure";
const string UrlOptionName = "--url";
const string UrlOptionDescription = "URL to inspect";
const string ProfileCommandName = "profile";
const string ProfileCommandDescription = "Show or switch MongoDB connection profiles";
const string ListAvailableProfilesDescription = "List available profiles";
const string MongoDbProfileEnvVar = "SADDLERAG_MONGODB_PROFILE";
const string ScanCommandName = "scan";
const string ScanCommandDescription = "Scan project dependencies and index documentation";
const string PathOptionName = "--path";
const string PathOptionDescription = "Path to project root, .sln, .csproj, package.json, or requirements.txt";
const string ProfileOptionName = "--profile";
const string ProfileOptionDescription = "Database profile name";

#endregion

// Build configuration
var configuration = new ConfigurationBuilder()
                    .AddJsonFile(AppSettingsFile, optional: true)
                    .AddEnvironmentVariables(EnvironmentVariablePrefix)
                    .Build();

// Build DI container
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole());
services.AddSaddleRagDatabase(configuration);
services.Configure<OllamaSettings>(configuration.GetSection(OllamaSettings.SectionName));
services.AddSingleton<OllamaBootstrapper>();
services.AddSingleton<GitHubRepoScraper>();
services.AddSingleton<PageCrawler>();
services.AddSingleton<IPageCrawler>(sp => sp.GetRequiredService<PageCrawler>());
services.AddSingleton<IScrapeAuditWriter>(sp =>
                                              new ScrapeAuditWriter(sp.GetRequiredService<IScrapeAuditRepository>())
                                         );
services.AddSingleton<LlmClassifier>();
services.AddSingleton<SymbolExtractor>();
services.AddSingleton<LibraryProfileService>();
services.AddSingleton<CliReconFallback>();
services.AddSingleton<RescrubService>();
services.AddSingleton<CategoryAwareChunker>();
services.AddSingleton<IEmbeddingProvider, OllamaEmbeddingProvider>();
services.AddSingleton<IVectorSearchProvider, InMemoryBruteForceVectorSearch>();
services.AddSingleton<SuspectDetector>();
services.AddSingleton<IngestionOrchestrator>();

// Scrape job queue (required by DependencyIndexer)
services.AddSingleton<ScrapeJobRunner>();
services.AddSingleton<IScrapeJobQueue>(sp =>
                                           sp.GetRequiredService<ScrapeJobRunner>()
                                      );

// HTTP clients for package registry APIs
services.AddHttpClient(NuGetClientName)
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                                                      {
                                                          AutomaticDecompression = DecompressionMethods.All
                                                      }
                                           );
services.AddHttpClient(NpmClientName);
services.AddHttpClient(PyPiClientName);
services.AddHttpClient(DocUrlProbeClientName);

// Shared utilities
services.AddSingleton<CommonDocUrlPatterns>();
services.AddSingleton<PackageFilter>();

// NuGet ecosystem
services.AddSingleton<IProjectFileParser, NuGetProjectFileParser>();
services.AddSingleton<IPackageRegistryClient, NuGetRegistryClient>();
services.AddSingleton<IDocUrlResolver, NuGetDocUrlResolver>();

// npm ecosystem
services.AddSingleton<IProjectFileParser, NpmProjectFileParser>();
services.AddSingleton<IPackageRegistryClient, NpmRegistryClient>();
services.AddSingleton<IDocUrlResolver, NpmDocUrlResolver>();

// pip ecosystem
services.AddSingleton<IProjectFileParser, PipProjectFileParser>();
services.AddSingleton<IPackageRegistryClient, PyPiRegistryClient>();
services.AddSingleton<IDocUrlResolver, PipDocUrlResolver>();

// Dependency indexing orchestrator
services.AddSingleton<DependencyIndexer>();

var provider = services.BuildServiceProvider();

// Root command
var rootCommand = new RootCommand(RootCommandDescription);

// ingest command
var ingestCommand = new Command(IngestCommandName, IngestCommandDescription);
var rootUrlOption = new Option<string>(RootUrlOptionName)
                        { Description = RootUrlOptionDescription, Required = true };
var libraryIdOption = new Option<string>(LibraryIdOptionName)
                          { Description = UniqueLibraryIdDescription, Required = true };
var versionOption = new Option<string>(VersionOptionName)
                        { Description = VersionStringDescription, Required = true };
var hintOption = new Option<string>(HintOptionName)
                     { Description = HintOptionDescription, Required = true };
var allowedOption = new Option<string[]>(AllowedOptionName)
                        {
                            Description = AllowedOptionDescription, Required = true,
                            AllowMultipleArgumentsPerToken = true
                        };
var excludedOption = new Option<string[]>(ExcludedOptionName)
                         { Description = ExcludedOptionDescription, AllowMultipleArgumentsPerToken = true };
var maxPagesOption = new Option<int>(MaxPagesOptionName)
                         { Description = MaxPagesOptionDescription, DefaultValueFactory = _ => 0 };
var delayOption = new Option<int>(DelayOptionName)
                      {
                          Description = DelayOptionDescription, DefaultValueFactory = _ => ScrapeJob.DefaultFetchDelayMs
                      };

ingestCommand.Options.Add(rootUrlOption);
ingestCommand.Options.Add(libraryIdOption);
ingestCommand.Options.Add(versionOption);
ingestCommand.Options.Add(hintOption);
ingestCommand.Options.Add(allowedOption);
ingestCommand.Options.Add(excludedOption);
ingestCommand.Options.Add(maxPagesOption);
ingestCommand.Options.Add(delayOption);

ingestCommand.SetAction(async (parseResult, ct) =>
                        {
                            var rootUrl = parseResult.GetValue(rootUrlOption) ??
                                          throw new
                                              InvalidOperationException($"Required option '{RootUrlOptionName}' missing"
                                                                       );
                            var libraryId = parseResult.GetValue(libraryIdOption) ??
                                            throw new
                                                InvalidOperationException($"Required option '{LibraryIdOptionName}' missing"
                                                                         );
                            var version = parseResult.GetValue(versionOption) ??
                                          throw new
                                              InvalidOperationException($"Required option '{VersionOptionName}' missing"
                                                                       );
                            var hint = parseResult.GetValue(hintOption) ??
                                       throw new InvalidOperationException($"Required option '{HintOptionName}' missing"
                                                                          );
                            var allowed = parseResult.GetValue(allowedOption) ??
                                          throw new
                                              InvalidOperationException($"Required option '{AllowedOptionName}' missing"
                                                                       );
                            var excluded = parseResult.GetValue(excludedOption);
                            var maxPages = parseResult.GetValue(maxPagesOption);
                            var delay = parseResult.GetValue(delayOption);

                            // Bootstrap Ollama: install if missing, start if stopped, pull required models
                            var bootstrapper = provider.GetRequiredService<OllamaBootstrapper>();
                            await bootstrapper.BootstrapAsync();

                            var job = new ScrapeJob
                                          {
                                              RootUrl = rootUrl,
                                              LibraryId = libraryId,
                                              Version = version,
                                              LibraryHint = hint,
                                              AllowedUrlPatterns = allowed,
                                              ExcludedUrlPatterns = excluded ?? [],
                                              MaxPages = maxPages,
                                              FetchDelayMs = delay
                                          };

                            var orchestrator = provider.GetRequiredService<IngestionOrchestrator>();
                            await orchestrator.IngestAsync(job,
                                                           onProgress: progress =>
                                                                       {
                                                                           Console
                                                                               .Write(IngestProgressFormatter
                                                                                          .Format(progress)
                                                                                     );
                                                                       }
                                                          );
                            Console.WriteLine();
                            return 0;
                        }
                       );

// list command
var listCommand = new Command(ListCommandName, ListAllLibrariesDescription);
listCommand.SetAction(async (parseResult, ct) =>
                          await ListLibrariesHandler.RunAsync(provider.GetRequiredService<ILibraryRepository>(),
                                                              Console.Out,
                                                              ct
                                                             )
                     );

// status command
var statusCommand = new Command(StatusCommandName, StatusCommandDescription);
var statusLibOption = new Option<string>(LibraryIdOptionName)
                          { Description = LibraryIdDescription, Required = true };
statusCommand.Options.Add(statusLibOption);
statusCommand.SetAction(async (parseResult, ct) =>
                            await StatusHandler.RunAsync(parseResult.GetValue(statusLibOption) ??
                                                         throw new
                                                             InvalidOperationException($"Required option '{LibraryIdOptionName}' missing"
                                                                                      ),
                                                         provider.GetRequiredService<ILibraryRepository>(),
                                                         provider.GetRequiredService<IPageRepository>(),
                                                         provider.GetRequiredService<IChunkRepository>(),
                                                         Console.Out,
                                                         ct
                                                        )
                       );

// dryrun command
var dryrunCommand = new Command(DryrunCommandName, DryrunCommandDescription);
dryrunCommand.Options.Add(rootUrlOption);
dryrunCommand.Options.Add(allowedOption);
dryrunCommand.Options.Add(excludedOption);
dryrunCommand.Options.Add(maxPagesOption);
dryrunCommand.Options.Add(delayOption);

dryrunCommand.SetAction(async (parseResult, ct) =>
                        {
                            var rootUrl = parseResult.GetValue(rootUrlOption) ??
                                          throw new
                                              InvalidOperationException($"Required option '{RootUrlOptionName}' missing"
                                                                       );
                            var allowed = parseResult.GetValue(allowedOption) ??
                                          throw new
                                              InvalidOperationException($"Required option '{AllowedOptionName}' missing"
                                                                       );
                            var excluded = parseResult.GetValue(excludedOption);
                            var maxPages = parseResult.GetValue(maxPagesOption);
                            var delay = parseResult.GetValue(delayOption);

                            var job = new ScrapeJob
                                          {
                                              RootUrl = rootUrl,
                                              LibraryId = "dryrun",
                                              Version = "dryrun",
                                              LibraryHint = "Dry run",
                                              AllowedUrlPatterns = allowed,
                                              ExcludedUrlPatterns = excluded ?? [],
                                              MaxPages = maxPages,
                                              FetchDelayMs = delay
                                          };

                            var orchestrator = provider.GetRequiredService<IngestionOrchestrator>();
                            var report = await orchestrator.DryRunAsync(job,
                                                                        libraryId: DryrunCommandName,
                                                                        version: DryrunCommandName,
                                                                        jobId: Guid.NewGuid().ToString("N"),
                                                                        onProgress: null,
                                                                        ct: ct
                                                                       );

                            return DryrunReportRenderer.Render(report, maxPages, Console.Out);
                        }
                       );

// reclassify command — re-run LLM classifier over already-ingested pages
var reclassifyCommand = new Command(ReclassifyCommandName,
                                    ReclassifyCommandDescription
                                   );
var reclassifyLibOption = new Option<string?>(LibraryIdOptionName)
                              { Description = ReclassifyLibraryIdDescription };
var reclassifyAllOption = new Option<bool>(AllOptionName)
                              { Description = ReclassifyAllDescription, DefaultValueFactory = _ => false };
reclassifyCommand.Options.Add(reclassifyLibOption);
reclassifyCommand.Options.Add(reclassifyAllOption);

reclassifyCommand.SetAction(async (parseResult, ct) =>
                            {
                                var libraryId = parseResult.GetValue(reclassifyLibOption);
                                var allPages = parseResult.GetValue(reclassifyAllOption);

                                await provider.GetRequiredService<OllamaBootstrapper>().BootstrapAsync();

                                return await ReclassifyHandler.RunAsync(libraryId,
                                                                        allPages,
                                                                        provider
                                                                            .GetRequiredService<ILibraryRepository>(),
                                                                        provider
                                                                            .GetRequiredService<IPageRepository>(),
                                                                        provider
                                                                            .GetRequiredService<IChunkRepository>(),
                                                                        provider
                                                                            .GetRequiredService<LlmClassifier>(),
                                                                        Console.Out,
                                                                        ct
                                                                       );
                            }
                           );

// inspect command — load a single page, dump TOC/sidebar info
var inspectCommand = new Command(InspectCommandName, InspectCommandDescription);
var inspectUrlOption = new Option<string>(UrlOptionName)
                           { Description = UrlOptionDescription, Required = true };
inspectCommand.Options.Add(inspectUrlOption);
inspectCommand.SetAction(async (parseResult, ct) =>
                         {
                             var url = parseResult.GetValue(inspectUrlOption) ??
                                       throw new InvalidOperationException($"Required option '{UrlOptionName}' missing"
                                                                          );
                             using var playwright = await Playwright.CreateAsync();
                             await using var browser =
                                 await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }
                                                                      );
                             var page = await browser.NewPageAsync();

                             Console.WriteLine($"Loading {url}...");
                             await page.GotoAsync(url,
                                                  new PageGotoOptions
                                                      {
                                                          WaitUntil = WaitUntilState.NetworkIdle,
                                                          Timeout = 60000
                                                      }
                                                 );

                             // Extra wait for deferred JS (like TOC loaders)
                             await Task.Delay(millisecondsDelay: 3000);

                             var infoJson = await page.EvaluateAsync<string>(expression: """
                                                                                 (() => {
                                                                                     const result = {};
                                                                                     result.totalLinks = document.querySelectorAll('a[href]').length;
                                                                                     result.title = document.title;

                                                                                     const candidates = [
                                                                                         'nav', 'aside',
                                                                                         '[class*="sidebar" i]', '[class*="toc" i]', '[class*="tree" i]',
                                                                                         '[id*="sidebar" i]', '[id*="toc" i]', '[id*="tree" i]', '[id*="nav" i]',
                                                                                         '.left-nav', '.left-menu', '.doc-nav', '.help-nav'
                                                                                     ];
                                                                                     const sidebars = [];
                                                                                     const seen = new Set();
                                                                                     for (const sel of candidates) {
                                                                                         try {
                                                                                             const els = document.querySelectorAll(sel);
                                                                                             for (const el of els) {
                                                                                                 if (seen.has(el)) continue;
                                                                                                 seen.add(el);
                                                                                                 const linkCount = el.querySelectorAll('a[href]').length;
                                                                                                 if (linkCount > 5) {
                                                                                                     sidebars.push({
                                                                                                         sel: sel,
                                                                                                         tag: el.tagName,
                                                                                                         id: el.id || '',
                                                                                                         className: ((el.className || '') + '').substring(0, 120),
                                                                                                         linkCount: linkCount,
                                                                                                         samples: Array.from(el.querySelectorAll('a[href]')).slice(0, 3).map(function(a) { return a.href; })
                                                                                                     });
                                                                                                 }
                                                                                             }
                                                                                         } catch (e) {}
                                                                                     }
                                                                                     result.sidebars = sidebars;

                                                                                     result.collapsed = {
                                                                                         ariaCollapsed: document.querySelectorAll('[aria-expanded="false"]').length,
                                                                                         collapsedClass: document.querySelectorAll('.collapsed').length,
                                                                                         hiddenClass: document.querySelectorAll('.hidden, .hide').length,
                                                                                         treeNodes: document.querySelectorAll('[class*="tree-node" i], [class*="treenode" i]').length
                                                                                     };

                                                                                     const hosts = {};
                                                                                     document.querySelectorAll('a[href]').forEach(function(a) {
                                                                                         try {
                                                                                             const u = new URL(a.href);
                                                                                             hosts[u.host] = (hosts[u.host] || 0) + 1;
                                                                                         } catch (e) {}
                                                                                     });
                                                                                     result.linksByHost = hosts;

                                                                                     return JSON.stringify(result);
                                                                                 })()
                                                                                 """
                                                                            );

                             using var doc = JsonDocument.Parse(infoJson);
                             var exit = InspectReportRenderer.Render(doc.RootElement, Console.Out);

                             await page.CloseAsync();
                             return exit;
                         }
                        );

// profile command
var profileCommand = new Command(ProfileCommandName, ProfileCommandDescription);

var profileListCommand = new Command(ListCommandName, ListAvailableProfilesDescription);
profileListCommand.SetAction(parseResult =>
                                 ProfileListHandler.Run(provider.GetRequiredService<IOptions<SaddleRagDbSettings>>()
                                                                .Value,
                                                        Environment.GetEnvironmentVariable(MongoDbProfileEnvVar),
                                                        Console.Out
                                                       )
                            );

profileCommand.Subcommands.Add(profileListCommand);

// scan command — scan project dependencies and index documentation
var scanCommand = new Command(ScanCommandName, ScanCommandDescription);
var scanPathOption = new Option<string>(PathOptionName)
                         { Description = PathOptionDescription, Required = true };
var scanProfileOption = new Option<string?>(ProfileOptionName)
                            { Description = ProfileOptionDescription };
scanCommand.Options.Add(scanPathOption);
scanCommand.Options.Add(scanProfileOption);

scanCommand.SetAction(async (parseResult, ct) =>
                      {
                          var path = parseResult.GetValue(scanPathOption) ??
                                     throw new InvalidOperationException($"Required option '{PathOptionName}' missing");
                          var profile = parseResult.GetValue(scanProfileOption);

                          var indexer = provider.GetRequiredService<DependencyIndexer>();
                          var report = await indexer.IndexProjectAsync(path, profile, ct: ct);

                          return ScanReportRenderer.Render(report, Console.Out);
                      }
                     );

rootCommand.Subcommands.Add(ingestCommand);
rootCommand.Subcommands.Add(dryrunCommand);
rootCommand.Subcommands.Add(inspectCommand);
rootCommand.Subcommands.Add(reclassifyCommand);
rootCommand.Subcommands.Add(RegisterClientsCommand.Build());
rootCommand.Subcommands.Add(UnregisterClientsCommand.Build());
rootCommand.Subcommands.Add(StatusCommand.Build());
rootCommand.Subcommands.Add(scanCommand);
rootCommand.Subcommands.Add(listCommand);
rootCommand.Subcommands.Add(statusCommand);
rootCommand.Subcommands.Add(profileCommand);

return await rootCommand.Parse(args).InvokeAsync();
