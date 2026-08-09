// Stage 9 acceptance coverage.
// Proposed seam: IDirectoryDocumentCapabilityPreflight is injected into
// DirectoryScanJobRunner and runs after registration/binding lookup but before
// version capture, job creation, candidate writes, or coordinator execution.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Documents.Docling;
using SaddleRAG.Ingestion.Scanning;
using SaddleRAG.Tests.Ingestion;

namespace SaddleRAG.Tests.Models;

public sealed class DocumentScanPreflightTests
{
    [Fact]
    public async Task PdfScanWithoutReadyDoclingReturnsObservedReasonBeforeAnyJobOrCandidateWrite()
    {
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var jobs = Substitute.For<IJobRepository>();
        var sources = Substitute.For<ISourceDocumentRepository>();
        var libraries = Substitute.For<ILibraryRepository>();
        var coordinator = Substitute.For<IDirectoryIngestionCoordinator>();
        var preflight = Substitute.For<IDirectoryDocumentCapabilityPreflight>();
        var cancellation = Substitute.For<IJobCancellationRegistry>();
        var lifetime = StoppedNeverLifetime();
        DirectoryLibraryDefinition definition = Definition();
        var existing = new LibraryRecord
                           {
                               Id = LibraryId,
                               Name = "Manuals",
                               Hint = "shop manuals",
                               CurrentVersion = ExistingVersion,
                               AllVersions = [ExistingVersion]
                           };

        factory.GetSourceDocumentRepository(null).Returns(sources);
        factory.GetJobRepository(null).Returns(jobs);
        factory.GetLibraryRepository(null).Returns(libraries);
        sources.GetDirectoryDefinitionAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(definition);
        libraries.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(existing);
        DoclingCapabilityStatus observed = Unavailable(DoclingReasonCodes.ModelsUnavailable,
                                                       "Docling responded, but its models are unavailable.");
        preflight.EvaluateAsync(definition, Arg.Any<CancellationToken>())
                 .Returns(DocumentCapabilityPreflightResult.Blocked(observed,
                                                                   DoclingInstallInstructions.OfficialInstallUrl,
                                                                   DoclingInstallInstructions.OfficialReleaseUrl));
        var runner = new DirectoryScanJobRunner(coordinator,
                                                new DirectoryScanVersionProvider(TimeProvider.System),
                                                factory,
                                                cancellation,
                                                lifetime,
                                                preflight,
                                                NullLogger<DirectoryScanJobRunner>.Instance);

        DirectoryScanQueueResult result = await runner.QueueAsync(LibraryId,
                                                                   profile: null,
                                                                   TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryScanQueueStatuses.Failed, result.Status);
        Assert.Equal(observed.ReasonCode, result.ReasonCode);
        Assert.Equal(observed.Detail, result.Detail);
        Assert.Equal(DoclingInstallInstructions.OfficialInstallUrl, result.OfficialInstallUrl);
        Assert.Equal(DoclingInstallInstructions.OfficialReleaseUrl, result.OfficialReleaseUrl);
        Assert.Null(result.JobId);
        Assert.Equal(ExistingVersion, existing.CurrentVersion);

        CancellationToken testToken = TestContext.Current.CancellationToken;
        await jobs.DidNotReceiveWithAnyArgs().UpsertAsync(default!, testToken);
        await sources.DidNotReceiveWithAnyArgs().GetOrCreateDocumentAsync(default!, testToken);
        await sources.DidNotReceiveWithAnyArgs().PersistRevisionAsync(default!, default!, default, testToken);
        await sources.DidNotReceiveWithAnyArgs().SetRevisionStateAsync(default!, default!, default, testToken);
        await libraries.DidNotReceiveWithAnyArgs().UpsertVersionAsync(default!, testToken);
        await libraries.DidNotReceiveWithAnyArgs().UpsertLibraryAsync(default!, testToken);
        await coordinator.DidNotReceiveWithAnyArgs().RunAsync(default!, default, testToken);
    }

    [Fact]
    public async Task MetadataOnlyPreflightFindsNestedPdfAndDoesNotReadDocumentBytes()
    {
        var fileSystem = new ScriptedDirectoryScanFileSystem();
        string nested = Path.Combine(RootPath, "nested");
        fileSystem.SetEnumeration(RootPath,
                                  Success(Directory(nested)));
        fileSystem.SetEnumeration(nested,
                                  Success(File(Path.Combine(nested, "service.pdf"))));
        var capability = Substitute.For<IDoclingCapabilityService>();
        capability.GetStatusAsync(true, Arg.Any<CancellationToken>())
                  .Returns(Unavailable(DoclingReasonCodes.EndpointUnreachable, "connection refused"));
        var sut = new DirectoryDocumentCapabilityPreflight(fileSystem, capability);

        DocumentCapabilityPreflightResult result = await sut.EvaluateAsync(Definition(),
                                                                            TestContext.Current.CancellationToken);

        Assert.False(result.Allowed);
        Assert.True(result.RequiresDocling);
        Assert.Equal(DoclingReasonCodes.EndpointUnreachable, result.Status!.ReasonCode);
        Assert.Empty(fileSystem.ReadPaths);
        await capability.Received(1).GetStatusAsync(true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TextAndHtmlOnlyDirectoryDoesNotRequireOrProbeDocling()
    {
        var fileSystem = new ScriptedDirectoryScanFileSystem();
        fileSystem.SetEnumeration(RootPath,
                                  Success(File(Path.Combine(RootPath, "readme.txt")),
                                          File(Path.Combine(RootPath, "index.html")),
                                          File(Path.Combine(RootPath, "notes.md"))));
        var capability = Substitute.For<IDoclingCapabilityService>();
        var sut = new DirectoryDocumentCapabilityPreflight(fileSystem, capability);

        DocumentCapabilityPreflightResult result = await sut.EvaluateAsync(Definition(),
                                                                            TestContext.Current.CancellationToken);

        Assert.True(result.Allowed);
        Assert.False(result.RequiresDocling);
        Assert.Null(result.Status);
        Assert.Empty(fileSystem.ReadPaths);
        await capability.DidNotReceiveWithAnyArgs()
                        .GetStatusAsync(default, TestContext.Current.CancellationToken);
    }

    private static DirectoryEnumerationResult Success(params DirectoryEntrySnapshot[] entries) =>
        new(entries, string.Empty, null);

    private static DirectoryEntrySnapshot Directory(string path) =>
        new(path, FileAttributes.Directory, 0, DateTime.UtcNow);

    private static DirectoryEntrySnapshot File(string path) =>
        new(path, FileAttributes.Normal, 100, DateTime.UtcNow);

    private static DirectoryLibraryDefinition Definition() => new()
        {
            Id = LibraryId,
            RootPath = RootPath,
            Recursive = true,
            AllowedExtensions = DirectoryScanLimits.SupportedExtensions,
            ExclusionPatterns = [],
            BindingStatus = DirectoryLibraryBindingStatus.Bound,
            RegisteredAtUtc = DateTime.UtcNow
        };

    private static DoclingCapabilityStatus Unavailable(string reason, string detail) =>
        new(DoclingCapabilityState.Unavailable,
            reason,
            detail,
            "http://localhost:5001",
            DateTimeOffset.UtcNow,
            "Install or repair the user-managed Docling endpoint, then test it again.");

    private static IHostApplicationLifetime StoppedNeverLifetime()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Returns(CancellationToken.None);
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        lifetime.ApplicationStopped.Returns(CancellationToken.None);
        return lifetime;
    }

    private const string RootPath = "C:\\owned-manuals";
    private const string LibraryId = "manual-library";
    private const string ExistingVersion = "2026-08-03";
}
