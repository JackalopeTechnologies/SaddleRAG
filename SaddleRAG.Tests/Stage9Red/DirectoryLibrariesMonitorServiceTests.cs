// Stage 9 acceptance coverage.
// Proposed seam: a focused DirectoryLibraryMonitorDataService avoids widening the
// established MonitorDataService constructor used by every existing monitor test.

using System.Net;
using System.Text;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Documents.Docling;
using SaddleRAG.Ingestion.Scanning;
using SaddleRAG.Monitor.Services;

namespace SaddleRAG.Tests.Monitor;

public sealed class DirectoryLibrariesMonitorServiceTests
{
    [Fact]
    public async Task ListReturnsUserControlledRootOptionsLastSuccessAndSanitizedFileFailures()
    {
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var sources = Substitute.For<ISourceDocumentRepository>();
        var libraries = Substitute.For<ILibraryRepository>();
        var jobs = Substitute.For<IJobRepository>();
        factory.GetSourceDocumentRepository(null).Returns(sources);
        factory.GetLibraryRepository(null).Returns(libraries);
        factory.GetJobRepository(null).Returns(jobs);
        sources.GetDirectoryDefinitionsAsync(Arg.Any<CancellationToken>())
               .Returns(new[] { Definition() });
        libraries.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
                 .Returns(new LibraryRecord
                              {
                                  Id = LibraryId,
                                  Name = "Service Manuals",
                                  Hint = "maintenance and setup",
                                  CurrentVersion = Version,
                                  AllVersions = [Version]
                              });
        libraries.GetVersionAsync(LibraryId, Version, Arg.Any<CancellationToken>())
                 .Returns(VersionRecord());
        jobs.ListRecentAsync(JobType.DirectoryScan, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { ScanJob() });
        var sut = new DirectoryLibraryMonitorDataService(factory);

        IReadOnlyList<DirectoryLibraryMonitorRow> rows = await sut.ListAsync(
                                                                 profile: null,
                                                                 TestContext.Current.CancellationToken);

        DirectoryLibraryMonitorRow row = Assert.Single(rows);
        Assert.Equal(LibraryId, row.LibraryId);
        Assert.Equal("Service Manuals", row.Name);
        Assert.Equal("maintenance and setup", row.Hint);
        Assert.Equal(RootPath, row.RootPath);
        Assert.True(row.Recursive);
        Assert.Equal(DirectoryScanLimits.SupportedExtensions, row.AllowedExtensions);
        Assert.Equal(Version, row.LastSuccessfulVersion);
        Assert.Equal(ScrapedAt, row.LastSuccessfulAt);
        Assert.Equal("job-7", row.LatestJobId);
        Assert.Equal("Failed", row.LatestJobStatus);
        Assert.Equal(3, row.Progress!.DocumentsCompleted);
        DirectoryScanFileFailure failure = Assert.Single(row.FileFailures);
        Assert.Equal("nested/broken.pdf", failure.RelativePath);
        Assert.Equal(DoclingReasonCodes.ConversionFailed, failure.ReasonCode);
        Assert.DoesNotContain(RootPath, failure.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteServiceUsesSeparateTestRegisterAndExplicitManualScanEndpoints()
    {
        var handler = new RecordingHandler();
        var sut = new MonitorWriteService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });

        await sut.TestDirectoryAccessAsync(RootPath, recursive: true, TestContext.Current.CancellationToken);
        await sut.RegisterDirectoryLibraryAsync(new DirectoryRegistrationRequest(LibraryId,
                                                                                 RootPath,
                                                                                 Recursive: true,
                                                                                 ExclusionPatterns: []),
                                                profile: null,
                                                TestContext.Current.CancellationToken);
        await sut.ScanDirectoryLibraryAsync(LibraryId,
                                            profile: null,
                                            TestContext.Current.CancellationToken);

        Assert.Collection(handler.Requests,
                          test =>
                              {
                                  Assert.Equal(HttpMethod.Post, test.Method);
                                  Assert.Equal("/api/monitor/directory-libraries/test-access", test.Uri.PathAndQuery);
                                  Assert.Contains(RootPath.Replace("\\", "\\\\"), test.Body, StringComparison.Ordinal);
                              },
                          register =>
                              {
                                  Assert.Equal(HttpMethod.Post, register.Method);
                                  Assert.Equal("/api/monitor/directory-libraries/register", register.Uri.PathAndQuery);
                                  Assert.Contains(LibraryId, register.Body, StringComparison.Ordinal);
                                  Assert.Contains(RootPath.Replace("\\", "\\\\"), register.Body, StringComparison.Ordinal);
                              },
                          scan =>
                              {
                                  Assert.Equal(HttpMethod.Post, scan.Method);
                                  Assert.Equal($"/api/monitor/directory-libraries/{LibraryId}/scan",
                                               scan.Uri.PathAndQuery);
                                  Assert.DoesNotContain(RootPath, scan.Body, StringComparison.OrdinalIgnoreCase);
                              });
    }

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

    private static LibraryVersionRecord VersionRecord() => new()
        {
            Id = $"{LibraryId}-{Version}",
            LibraryId = LibraryId,
            Version = Version,
            ScrapedAt = ScrapedAt,
            PageCount = 4,
            ChunkCount = 20,
            EmbeddingProviderId = "test",
            EmbeddingModelName = "test",
            EmbeddingDimensions = 4,
            PublicationState = VersionPublicationState.Published
        };

    private static JobRecord ScanJob() => new()
        {
            Id = "job-7",
            JobType = JobType.DirectoryScan,
            LibraryId = LibraryId,
            Version = Version,
            Status = JobStatus.Failed,
            CreatedAt = ScrapedAt,
            ErrorCount = 1,
            DirectoryScanProgress = new DirectoryScanJobProgress
                                        {
                                            FilesDiscovered = 7,
                                            SupportedDocuments = 4,
                                            DocumentsCompleted = 3,
                                            CurrentRelativePath = "nested/broken.pdf"
                                        },
            DirectoryScanFailures =
            [
                new DirectoryScanFileFailure("nested/broken.pdf",
                                             DoclingReasonCodes.ConversionFailed,
                                             "Conversion failed under [registered root].")
            ]
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                      CancellationToken cancellationToken)
        {
            string body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
                       {
                           Content = new StringContent("{}", Encoding.UTF8, "application/json")
                       };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);

    private static readonly DateTime ScrapedAt = new(2026, 8, 4, 17, 0, 0, DateTimeKind.Utc);
    private const string RootPath = "D:\\User Libraries\\Service Manuals";
    private const string LibraryId = "service-manuals";
    private const string Version = "2026-08-04";
}
