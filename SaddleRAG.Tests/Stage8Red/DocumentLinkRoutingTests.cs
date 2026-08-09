// DocumentLinkRoutingTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.
// Stage 8 acceptance contract.

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Crawling;
using SaddleRAG.Ingestion.Documents.Docling;
using SaddleRAG.Ingestion.Documents.Intake;

namespace SaddleRAG.Tests.Crawling;

/// <summary>
///     Stage 8 RED draft. Rename to .cs only when Stage 8 begins. Web
///     documents must enter the existing IPageCrawler ChannelWriter&lt;PageRecord&gt;
///     boundary so the source-neutral ingestion processor remains the only
///     classify/chunk/embed/index/finalize path.
/// </summary>
public sealed class DocumentLinkRoutingTests
{
    [Fact]
    public async Task MixedSiteRoutesPdfDocxAndExtensionlessPdfWithoutTreatingHtmlSuffixAsBinary()
    {
        await using LoopbackDocumentSite site = LoopbackDocumentSite.Start();
        CrawlerFixture fixture = CreateFixture();

        IReadOnlyList<PageRecord> pages = await CrawlAsync(fixture.Crawler,
                                                           site.MixedIndexUrl,
                                                           TestContext.Current.CancellationToken);

        Assert.Contains(fixture.Intake.Requests,
                        request => request.Content.Span.SequenceEqual(site.PdfBody));
        Assert.Contains(fixture.Intake.Requests,
                        request => request.Content.Span.SequenceEqual(site.DocxBody));
        Assert.Contains(fixture.Intake.Requests,
                        request => request.Content.Span.SequenceEqual(site.ExtensionlessPdfBody));
        Assert.DoesNotContain(fixture.Intake.Requests,
                              request => Encoding.UTF8.GetString(request.Content.Span)
                                                 .Contains(MisleadingHtmlMarker, StringComparison.Ordinal));
        Assert.Contains(pages,
                        page => page.DocumentSource?.SourceUri == site.ManualPdfUrl);
        Assert.Contains(pages,
                        page => page.DocumentSource?.SourceUri == site.ManualDocxUrl);
        Assert.Contains(pages,
                        page => page.DocumentSource?.SourceUri == site.ExtensionlessPdfUrl);
        Assert.Contains(pages,
                        page => page.DocumentSource == null
                                && page.RawContent.Contains(MisleadingHtmlMarker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RedirectPreservesOriginalAttemptedAndFinalUrlProvenance()
    {
        await using LoopbackDocumentSite site = LoopbackDocumentSite.Start();
        CrawlerFixture fixture = CreateFixture();

        IReadOnlyList<PageRecord> pages = await CrawlAsync(fixture.Crawler,
                                                           site.RedirectUrl,
                                                           TestContext.Current.CancellationToken);

        PageRecord documentPage = Assert.Single(pages, page => page.DocumentSource != null);
        DocumentProvenance documentSource = Assert.IsType<DocumentProvenance>(documentPage.DocumentSource);
        Assert.Equal(site.RedirectUrl, documentSource.SourceUri);
        Assert.Equal(site.RedirectUrl, documentSource.OriginalUrl);
        Assert.Equal(site.RedirectUrl, documentSource.AttemptedUrl);
        Assert.Equal(site.ManualPdfUrl, documentSource.FinalUrl);
        Assert.StartsWith(site.ManualPdfUrl, documentPage.Url, StringComparison.Ordinal);
        DocumentRevisionRecord revision = Assert.Single(fixture.Sources.Revisions);
        Assert.Equal(site.RedirectUrl, revision.OriginalUrl);
        Assert.Equal(site.RedirectUrl, revision.AttemptedUrl);
        Assert.Equal(site.ManualPdfUrl, revision.FinalUrl);
    }

    [Fact]
    public async Task RetainedOriginalAndIntakeInputAreTheExactResponseBodyBytes()
    {
        await using LoopbackDocumentSite site = LoopbackDocumentSite.Start();
        CrawlerFixture fixture = CreateFixture();

        await CrawlAsync(fixture.Crawler,
                         site.ManualPdfUrl,
                         TestContext.Current.CancellationToken);

        DocumentIntakeRequest intake = Assert.Single(fixture.Intake.Requests);
        Assert.Equal(site.PdfBody, intake.Content.ToArray());
        DocumentRevisionRecord revision = Assert.Single(fixture.Sources.Revisions);
        Assert.Equal(site.PdfBody, fixture.Sources.OriginalArtifacts[revision.OriginalArtifactHash]);
        Assert.Equal(site.PdfBody.LongLength, revision.OriginalByteLength);
        Assert.Equal("\"owned-pdf-v1\"", revision.SourceETag);
        Assert.Equal(new DateTime(year: 2026,
                                  month: 8,
                                  day: 4,
                                  hour: 18,
                                  minute: 0,
                                  second: 0,
                                  DateTimeKind.Utc),
                     revision.SourceLastModifiedAtUtc);
    }

    [Fact]
    public async Task SupportedDocumentFailureFaultsTheCommonPageChannelAndPreventsPublication()
    {
        await using LoopbackDocumentSite site = LoopbackDocumentSite.Start();
        CrawlerFixture fixture = CreateFixture();
        Channel<PageRecord> channel = Channel.CreateUnbounded<PageRecord>();

        SupportedDocumentIngestionException error = await Assert.ThrowsAsync<SupportedDocumentIngestionException>(
            () => ((IPageCrawler)fixture.Crawler).CrawlAsync(Job(site.BrokenIndexUrl),
                                                            channel.Writer,
                                                            ct: TestContext.Current.CancellationToken));

        Assert.Equal(DoclingReasonCodes.ConversionFailed, error.ReasonCode);
        Assert.Contains("scripted extraction failure", error.Detail, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
                                                   await channel.Reader.Completion.WaitAsync(
                                                       TimeSpan.FromSeconds(ChannelCompletionTimeoutSeconds),
                                                       TestContext.Current.CancellationToken));
        await fixture.Pages.DidNotReceive()
                     .UpsertPageAsync(Arg.Is<PageRecord>(page => HasDocumentSourceUri(page,
                                                                                     site.BrokenPdfUrl)),
                                       Arg.Any<CancellationToken>());
        Assert.DoesNotContain(fixture.Sources.Revisions,
                              revision => revision.State == DocumentRevisionState.Published);
    }

    [Fact]
    public async Task HtmlOnlyLibraryUsesExistingPagePathWhenDocumentCapabilityIsAbsent()
    {
        await using LoopbackDocumentSite site = LoopbackDocumentSite.Start();
        CrawlerFixture fixture = CreateFixture(throwIfDocumentIntakeIsCalled: true);

        IReadOnlyList<PageRecord> pages = await CrawlAsync(fixture.Crawler,
                                                           site.HtmlOnlyIndexUrl,
                                                           TestContext.Current.CancellationToken);

        Assert.Contains(pages,
                        page => page.RawContent.Contains(HtmlRootMarker, StringComparison.Ordinal));
        Assert.Contains(pages,
                        page => page.RawContent.Contains(HtmlChildMarker, StringComparison.Ordinal));
        Assert.All(pages, page => Assert.Null(page.DocumentSource));
        Assert.Empty(fixture.Intake.Requests);
    }

    private static CrawlerFixture CreateFixture(bool throwIfDocumentIntakeIsCalled = false)
    {
        var pages = Substitute.For<IPageRepository>();
        var audit = Substitute.For<IScrapeAuditWriter>();
        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        var intake = new ScriptedDocumentIntake(throwIfDocumentIntakeIsCalled);
        var sources = new RecordingSourceDocumentRepository();
        var documentProducer = new WebDocumentPageProducer(sources,
                                                           intake,
                                                           TimeProvider.System,
                                                           NullLogger<WebDocumentPageProducer>.Instance);
        var crawler = new PageCrawler(pages,
                                      new GitHubRepoScraper(pages, NullLogger<GitHubRepoScraper>.Instance),
                                      audit,
                                      broadcaster,
                                      NullLogger<PageCrawler>.Instance,
                                      NullLoggerFactory.Instance,
                                      documentProducer);
        return new CrawlerFixture(crawler, pages, intake, sources);
    }

    private static async Task<IReadOnlyList<PageRecord>> CrawlAsync(PageCrawler crawler,
                                                                    string rootUrl,
                                                                    CancellationToken ct)
    {
        Channel<PageRecord> channel = Channel.CreateUnbounded<PageRecord>();
        Task crawl = ((IPageCrawler)crawler).CrawlAsync(Job(rootUrl), channel.Writer, ct: ct);
        var pages = new List<PageRecord>();
        await foreach(PageRecord page in channel.Reader.ReadAllAsync(ct))
            pages.Add(page);
        await crawl;
        return pages;
    }

    private static ScrapeJob Job(string rootUrl)
    {
        var root = new Uri(rootUrl);
        string authority = root.GetLeftPart(UriPartial.Authority);
        return new ScrapeJob
                   {
                       RootUrl = rootUrl,
                       LibraryHint = "Owned mixed-format web documentation",
                       LibraryId = LibraryId,
                       Version = Version,
                       AllowedUrlPatterns = [$"^{Regex.Escape(authority)}/"],
                       ExcludedUrlPatterns = [],
                       MaxPages = MaxPages,
                       InScopeDepth = MaxDepth,
                       SameHostDepth = MaxDepth,
                       OffSiteDepth = 0
                   };
    }

    private sealed record CrawlerFixture(PageCrawler Crawler,
                                         IPageRepository Pages,
                                         ScriptedDocumentIntake Intake,
                                         RecordingSourceDocumentRepository Sources);

    private sealed class ScriptedDocumentIntake : IDocumentIntake
    {
        internal ScriptedDocumentIntake(bool throwIfCalled)
        {
            mThrowIfCalled = throwIfCalled;
        }

        private readonly bool mThrowIfCalled;
        private readonly ConcurrentQueue<DocumentIntakeRequest> mRequests = new();

        internal IReadOnlyList<DocumentIntakeRequest> Requests => mRequests.ToArray();

        public Task<DocumentIntakeResult> ReadAsync(DocumentIntakeRequest request,
                                                    CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mThrowIfCalled)
            {
                throw new InvalidOperationException(
                    "HTML-only crawling must not invoke document intake when Docling is absent.");
            }

            mRequests.Enqueue(request);
            string payload = Encoding.Latin1.GetString(request.Content.Span);
            DocumentIntakeResult result;
            if (payload.Contains(BrokenMarker, StringComparison.Ordinal))
            {
                result = new DocumentIntakeResult(Succeeded: false,
                                                  ReasonCode: DoclingReasonCodes.ConversionFailed,
                                                  Detail: "The scripted extraction failure is terminal.",
                                                  Title: string.Empty,
                                                  Sections: [],
                                                  ExtractionArtifact: ReadOnlyMemory<byte>.Empty,
                                                  ExtractionMediaType: "application/json",
                                                  Provenance: null);
            }
            else
            {
                string marker = MarkerFor(request.Content.Span);
                result = new DocumentIntakeResult(Succeeded: true,
                                                  ReasonCode: DocumentIntakeReasonCodes.Extracted,
                                                  Detail: "The owned response bytes were extracted.",
                                                  Title: request.FileName,
                                                  Sections:
                                                  [
                                                      new DocumentIntakeSection(Order: 0,
                                                                                Title: "Owned heading",
                                                                                Content: marker,
                                                                                PageStart: 1,
                                                                                PageEnd: 1)
                                                  ],
                                                  ExtractionArtifact: "{}"u8.ToArray(),
                                                  ExtractionMediaType: "application/json",
                                                  Provenance: new DocumentExtractionProvenance
                                                                  {
                                                                      ExtractorName = "scripted-web-intake",
                                                                      ExtractorVersion = "1"
                                                                  });
            }

            return Task.FromResult(result);
        }

        private static string MarkerFor(ReadOnlySpan<byte> content)
        {
            string payload = Encoding.Latin1.GetString(content);
            string result = payload.Contains("DOCX", StringComparison.Ordinal)
                                ? "SADDLERAG_WEB_DOCX"
                                : payload.Contains("EXTENSIONLESS", StringComparison.Ordinal)
                                    ? "SADDLERAG_WEB_EXTENSIONLESS_PDF"
                                    : "SADDLERAG_WEB_PDF";
            return result;
        }
    }

    private sealed class RecordingSourceDocumentRepository : ISourceDocumentRepository
    {
        private readonly ConcurrentDictionary<string, SourceDocumentRecord> mDocuments =
            new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, DocumentRevisionRecord> mRevisions =
            new(StringComparer.Ordinal);

        internal IReadOnlyCollection<DocumentRevisionRecord> Revisions => mRevisions.Values.ToArray();

        internal ConcurrentDictionary<string, byte[]> OriginalArtifacts { get; } =
            new(StringComparer.Ordinal);

        public Task UpsertDirectoryDefinitionAsync(DirectoryLibraryDefinition definition,
                                                   CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<DirectoryLibraryDefinition> RegisterDirectoryDefinitionAsync(
            DirectoryLibraryDefinition definition,
            CancellationToken ct = default) =>
            Task.FromResult(definition);

        public Task<DirectoryLibraryDefinition?> GetDirectoryDefinitionAsync(string libraryId,
                                                                              CancellationToken ct = default) =>
            Task.FromResult<DirectoryLibraryDefinition?>(null);

        public Task<IReadOnlyList<DirectoryLibraryDefinition>> GetDirectoryDefinitionsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DirectoryLibraryDefinition>>([]);

        public Task<IDirectoryPublicationLease?> TryAcquireDirectoryPublicationLeaseAsync(
            string libraryId,
            long registrationRevision,
            string? registrationIncarnationId,
            string scanRunId,
            string? expectedPublishedVersion,
            CancellationToken ct = default) =>
            Task.FromResult<IDirectoryPublicationLease?>(null);

        public Task<bool> TryUpdateDirectoryPublicationAsync(IDirectoryPublicationLease lease,
                                                              string? expectedPublishedVersion,
                                                              DateTime? publishedAtUtc,
                                                              string? publishedVersion,
                                                              CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> TryApplyDirectoryPackagePublicationAsync(
            IDirectoryPublicationLease lease,
            string? expectedPublishedVersion,
            DirectoryLibraryDefinition packageDefinition,
            DateTime publishedAtUtc,
            string publishedVersion,
            CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> TryRestoreDirectoryPublicationAsync(IDirectoryPublicationLease lease,
                                                               string failedPublishedVersion,
                                                               DateTime? restoredPublishedAtUtc,
                                                               string? restoredPublishedVersion,
                                                               CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> TryDeleteLeasedDirectoryDefinitionAsync(IDirectoryPublicationLease lease,
                                                                   CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<SourceDocumentRecord> GetOrCreateDocumentAsync(SourceDocumentRecord candidate,
                                                                   CancellationToken ct = default)
        {
            SourceDocumentRecord result = mDocuments.GetOrAdd(candidate.Id, candidate);
            return Task.FromResult(result);
        }

        public Task<SourceDocumentRecord?> GetDocumentAsync(string documentId,
                                                            CancellationToken ct = default)
        {
            mDocuments.TryGetValue(documentId, out SourceDocumentRecord? result);
            return Task.FromResult(result);
        }

        public async Task PersistRevisionAsync(DocumentRevisionRecord revision,
                                               Stream originalArtifact,
                                               Stream? extractionArtifact,
                                               CancellationToken ct = default)
        {
            OriginalArtifacts[revision.OriginalArtifactHash] = await ReadAllAsync(originalArtifact, ct);
            if (extractionArtifact != null)
                await ReadAllAsync(extractionArtifact, ct);
            mRevisions[revision.Id] = revision;
        }

        public Task<DocumentRevisionRecord?> GetRevisionAsync(string revisionId,
                                                               CancellationToken ct = default)
        {
            mRevisions.TryGetValue(revisionId, out DocumentRevisionRecord? result);
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<DocumentRevisionRecord>> GetRevisionsAsync(string libraryId,
                                                                              string version,
                                                                              CancellationToken ct = default)
        {
            IReadOnlyList<DocumentRevisionRecord> result = mRevisions.Values
                                                                     .Where(revision =>
                                                                                revision.LibraryId == libraryId
                                                                                && revision.Version == version)
                                                                     .OrderBy(revision => revision.Id,
                                                                              StringComparer.Ordinal)
                                                                     .ToList();
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<DocumentRevisionRecord>> GetRevisionsAsync(
            string libraryId,
            CancellationToken ct = default)
        {
            IReadOnlyList<DocumentRevisionRecord> result = mRevisions.Values
                                                                     .Where(revision =>
                                                                                revision.LibraryId == libraryId)
                                                                     .OrderBy(revision => revision.Id,
                                                                              StringComparer.Ordinal)
                                                                     .ToList();
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<LibraryVersionKey>> GetDistinctLibraryVersionPairsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LibraryVersionKey>>([]);

        public Task<IReadOnlyList<string>> GetArtifactHashesBecomingUnreferencedAsync(
            IReadOnlyCollection<string> deletingRevisionIds,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<Stream> OpenArtifactAsync(string sha256, CancellationToken ct = default)
        {
            Stream result = new MemoryStream(OriginalArtifacts[sha256], writable: false);
            return Task.FromResult(result);
        }

        public Task<bool> DeleteRevisionAsync(string revisionId, CancellationToken ct = default) =>
            Task.FromResult(mRevisions.TryRemove(revisionId, out _));

        public Task<DocumentArtifactRecoveryResult> RecoverArtifactClaimsAsync(
            DateTime utcNow,
            CancellationToken ct = default) =>
            Task.FromResult(new DocumentArtifactRecoveryResult(0, 0, 0));

        public Task<long> DeleteCandidateScanRunAsync(string libraryId,
                                                      string scanRunId,
                                                      CancellationToken ct = default) =>
            Task.FromResult(0L);

        public Task<long> DeleteVersionAsync(string libraryId,
                                             string version,
                                             CancellationToken ct = default) =>
            Task.FromResult(0L);

        public Task<long> DeleteLibraryAsync(string libraryId, CancellationToken ct = default) =>
            Task.FromResult(0L);

        public Task<long> SetRevisionStateAsync(string libraryId,
                                                 string version,
                                                 DocumentRevisionState state,
                                                CancellationToken ct = default)
        {
            long result = 0;
            foreach(KeyValuePair<string, DocumentRevisionRecord> item in mRevisions)
            {
                if (item.Value.LibraryId == libraryId && item.Value.Version == version)
                {
                    mRevisions[item.Key] = item.Value with { State = state };
                    result++;
                }
            }

            return Task.FromResult(result);
        }

        public Task<long> PublishCandidateScanRunAsync(string libraryId,
                                                       string version,
                                                       string scanRunId,
                                                       CancellationToken ct = default) =>
            Task.FromResult(0L);

        private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken ct)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, ct);
            return copy.ToArray();
        }
    }

    private static bool HasDocumentSourceUri(PageRecord? page, string sourceUri)
    {
        DocumentProvenance? documentSource = page?.DocumentSource;
        bool result = page != null
                      && documentSource != null
                      && string.Equals(documentSource.SourceUri, sourceUri, StringComparison.Ordinal);
        return result;
    }

    private const int MaxDepth = 4;
    private const int MaxPages = 16;
    private const int ChannelCompletionTimeoutSeconds = 5;
    private const string LibraryId = "owned-web-documents";
    private const string Version = "2026-08-04";
    private const string BrokenMarker = "SADDLERAG_WEB_BROKEN_PDF";
    private const string MisleadingHtmlMarker = "SADDLERAG_WEB_MISLEADING_HTML";
    private const string HtmlRootMarker = "SADDLERAG_HTML_ONLY_ROOT";
    private const string HtmlChildMarker = "SADDLERAG_HTML_ONLY_CHILD";
}
