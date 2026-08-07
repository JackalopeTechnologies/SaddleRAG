// DirectoryLibraryToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Scanning;
using SaddleRAG.Mcp.Tools;
using SaddleRAG.Tests.Ingestion;

namespace SaddleRAG.Tests.Mcp;

public sealed class DirectoryLibraryToolsTests
{
    [Fact]
    public async Task RegisterRequiresExplicitPathPersistsBoundDefinitionAndDoesNotScan()
    {
        var fileSystem = new ScriptedDirectoryScanFileSystem();
        fileSystem.SetInspection(RootPath,
                                 new DirectoryPathResult(new DirectoryEntrySnapshot(RootPath,
                                                                                    FileAttributes.Directory,
                                                                                    0,
                                                                                    RegisteredAt.UtcDateTime),
                                                         string.Empty,
                                                         null));
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var sources = Substitute.For<ISourceDocumentRepository>();
        factory.GetSourceDocumentRepository(Arg.Any<string?>()).Returns(sources);
        var service = new DirectoryLibraryRegistrationService(factory,
                                                              new DirectoryRootValidator(fileSystem),
                                                              new FixedRegistrationTimeProvider());

        DirectoryRegistrationResult result = await service.RegisterAsync(
                                                 new DirectoryRegistrationRequest(LibraryId,
                                                                                  RootPath,
                                                                                  Recursive: true,
                                                                                  ExclusionPatterns: ["**/bin/**"]),
                                                 profile: null,
                                                 TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryRegistrationStatuses.Registered, result.Status);
        await sources.Received(requiredNumberOfCalls: 1)
                     .RegisterDirectoryDefinitionAsync(
                         Arg.Is<DirectoryLibraryDefinition>(definition => definition != null
                                                                         && definition.Id == LibraryId
                                                                         && definition.RootPath == RootPath
                                                                         && definition.Recursive
                                                                         && definition.ExclusionPatterns.SequenceEqual(
                                                                             new[] { ExclusionPattern },
                                                                             StringComparer.Ordinal)
                                                                         && definition.BindingStatus ==
                                                                         DirectoryLibraryBindingStatus.Bound
                                                                         && definition.RegistrationRevision == 0
                                                                         && definition.RegisteredAtUtc ==
                                                                         RegisteredAt.UtcDateTime),
                         Arg.Any<CancellationToken>());
        Assert.Empty(fileSystem.EnumeratedPaths);
        Assert.Empty(fileSystem.ReadPaths);
    }

    [Fact]
    public async Task RegisterToolReturnsSanitizedResultAndNeverEchoesAbsoluteRoot()
    {
        var service = Substitute.For<IDirectoryLibraryRegistrationService>();
        service.RegisterAsync(Arg.Any<DirectoryRegistrationRequest>(),
                              Arg.Any<string?>(),
                              Arg.Any<CancellationToken>())
               .Returns(new DirectoryRegistrationResult(DirectoryRegistrationStatuses.Registered, LibraryId));

        string json = await DirectoryLibraryTools.RegisterDirectoryLibrary(service,
                                                                            LibraryId,
                                                                            RootPath,
                                                                            recursive: true,
                                                                            profile: null,
                                                                            TestContext.Current.CancellationToken);

        var result = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(DirectoryRegistrationStatuses.Registered, result["Status"]!.GetValue<string>());
        Assert.Equal(LibraryId, result["LibraryId"]!.GetValue<string>());
        Assert.DoesNotContain(RootPath, json, StringComparison.OrdinalIgnoreCase);
        await service.Received(requiredNumberOfCalls: 1)
                     .RegisterAsync(Arg.Is<DirectoryRegistrationRequest>(request => request != null
                                                                                  && request.LibraryId == LibraryId
                                                                                  && request.RootPath == RootPath
                                                                                  && request.Recursive),
                                    profile: null,
                                    Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterToolRejectsMissingExplicitPathBeforeCallingService()
    {
        var service = Substitute.For<IDirectoryLibraryRegistrationService>();

        await Assert.ThrowsAsync<ArgumentException>(() => DirectoryLibraryTools.RegisterDirectoryLibrary(
                                                              service,
                                                              LibraryId,
                                                              path: string.Empty,
                                                              recursive: true,
                                                              profile: null,
                                                              TestContext.Current.CancellationToken));

        await service.DidNotReceiveWithAnyArgs()
                     .RegisterAsync(default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ScanToolQueuesOnlyAnExplicitRequestForARegisteredLibrary()
    {
        var queue = Substitute.For<IDirectoryScanJobQueue>();
        queue.QueueAsync(LibraryId, profile: null, Arg.Any<CancellationToken>())
             .Returns(new DirectoryScanQueueResult(DirectoryScanQueueStatuses.Queued,
                                                   LibraryId,
                                                   Version,
                                                   JobId));

        string json = await DirectoryLibraryTools.ScanDirectoryLibrary(queue,
                                                                        LibraryId,
                                                                        profile: null,
                                                                        TestContext.Current.CancellationToken);

        var result = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(DirectoryScanQueueStatuses.Queued, result["Status"]!.GetValue<string>());
        Assert.Equal(Version, result["Version"]!.GetValue<string>());
        Assert.Equal(JobId, result["JobId"]!.GetValue<string>());
        await queue.Received(requiredNumberOfCalls: 1)
                   .QueueAsync(LibraryId, profile: null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnregisteredLibraryFailureIsReturnedWithoutStartingWork()
    {
        var queue = Substitute.For<IDirectoryScanJobQueue>();
        queue.QueueAsync("missing", profile: null, Arg.Any<CancellationToken>())
             .Returns(new DirectoryScanQueueResult(DirectoryScanQueueStatuses.Failed,
                                                   "missing",
                                                   string.Empty,
                                                   ReasonCode: DirectoryScanReasonCodes.LibraryNotRegistered,
                                                   Detail: "Register the directory library first."));

        string json = await DirectoryLibraryTools.ScanDirectoryLibrary(queue,
                                                                        "missing",
                                                                        profile: null,
                                                                        TestContext.Current.CancellationToken);

        var result = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(DirectoryScanQueueStatuses.Failed, result["Status"]!.GetValue<string>());
        Assert.Equal(DirectoryScanReasonCodes.LibraryNotRegistered,
                     result["ReasonCode"]!.GetValue<string>());
        Assert.Null(result["JobId"]);
    }

    [Fact]
    public void DirectoryIngestionDefinesNoHostedScannerWatcherOrTimerTrigger()
    {
        Assembly ingestionAssembly = typeof(DirectoryScanJobRunner).Assembly;
        IReadOnlyList<Type> automaticDirectoryServices = ingestionAssembly.GetTypes()
                                                                         .Where(type =>
                                                                             type.Name.Contains("Directory",
                                                                                 StringComparison.OrdinalIgnoreCase)
                                                                             && (typeof(IHostedService).IsAssignableFrom(
                                                                                     type)
                                                                                 || HasAutomaticTriggerField(type)))
                                                                         .ToList();

        Assert.Empty(automaticDirectoryServices);
    }

    private static bool HasAutomaticTriggerField(Type type) =>
        type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Any(field => field.FieldType == typeof(FileSystemWatcher)
                          || field.FieldType == typeof(Timer)
                          || field.FieldType == typeof(PeriodicTimer));

    private sealed class FixedRegistrationTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => RegisteredAt;
    }

    private static readonly DateTimeOffset RegisteredAt = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private const string RootPath = "C:\\owned-manuals";
    private const string ExclusionPattern = "**/bin/**";
    private const string LibraryId = "manual-library";
    private const string Version = "2026-08-04";
    private const string JobId = "directory-job-1";
}
