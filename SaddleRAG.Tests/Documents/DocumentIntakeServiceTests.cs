// DocumentIntakeServiceTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text;
using System.IO.Compression;
using SaddleRAG.Ingestion.Documents.Docling;
using SaddleRAG.Ingestion.Documents.Intake;

namespace SaddleRAG.Tests.Documents;

public sealed class DocumentIntakeServiceTests
{
    [Theory]
    [InlineData("guide.md", "text/markdown", "# Guide\r\nBody", "Guide")]
    [InlineData("guide.markdown", "text/markdown", "# Guide\r\nBody", "Guide")]
    [InlineData("notes.txt", "text/plain", "Notes\r\nBody", "notes")]
    [InlineData("notes.text", "text/plain", "Notes\r\nBody", "notes")]
    public async Task LocalMarkdownAndTextFormatsDoNotCallDocling(string fileName,
                                                                 string mediaType,
                                                                 string content,
                                                                 string expectedTitle)
    {
        var docling = Substitute.For<IDoclingClient>();
        var service = new DocumentIntakeService(docling);

        var result = await service.ReadAsync(Request(fileName, mediaType, Encoding.UTF8.GetBytes(content)),
                                             TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DocumentIntakeReasonCodes.Extracted, result.ReasonCode);
        Assert.Equal(expectedTitle, result.Title);
        Assert.False(string.IsNullOrWhiteSpace(result.Provenance?.ConfigurationHash));
        Assert.All(result.Sections, section => Assert.DoesNotContain('\r', section.Content));
        Assert.Empty(docling.ReceivedCalls());
    }

    [Theory]
    [InlineData("page.html")]
    [InlineData("page.htm")]
    public async Task HtmlUsesVisibleMainContentAndPreservesHeadingBoundaries(string fileName)
    {
        const string Html = "<html><head><title>Fallback</title><script>secret()</script></head>"
                            + "<body><nav>chrome</nav><main><h1>Manual</h1><p>Visible text.</p></main></body></html>";
        var docling = Substitute.For<IDoclingClient>();
        var service = new DocumentIntakeService(docling);

        var result = await service.ReadAsync(Request(fileName, "text/html", Encoding.UTF8.GetBytes(Html)),
                                             TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("Manual", result.Title);
        var content = string.Join('\n', result.Sections.Select(section => section.Content));
        Assert.Contains("# Manual", content, StringComparison.Ordinal);
        Assert.Contains("Visible text.", content, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chrome", content, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(docling.ReceivedCalls());
    }

    [Fact]
    public async Task LandmarkFreeHtmlSelectsDominantContentAndDropsSiblingAndWidgetChrome()
    {
        const string articleBody =
            "REAL_ARTICLE_MARKER Installer analytics reports which machines ran your installer, which "
            + "prerequisites were present, and how long each conversion stage took, so teams can prioritize "
            + "the environments customers actually use in production every day.";
        string html = "<html><head><title>Fallback</title></head><body>"
                      + "<header>MASTHEAD_MARKER Acme Corporation global site header</header>"
                      + "<div class=\"sidebar\"><ul><li>Home</li><li>SIDEBAR_MARKER</li></ul></div>"
                      + "<div id=\"rating-component\"><p>Did you find this page useful? RATING_MARKER</p></div>"
                      + $"<div id=\"tracking-software-docs\"><h1>Installer Analytics</h1><p>{articleBody}</p></div>"
                      + "</body></html>";

        string content = await ReadHtmlContentAsync(html);

        Assert.Contains("# Installer Analytics", content, StringComparison.Ordinal);
        Assert.Contains("REAL_ARTICLE_MARKER", content, StringComparison.Ordinal);
        Assert.DoesNotContain("SIDEBAR_MARKER", content, StringComparison.Ordinal);
        Assert.DoesNotContain("RATING_MARKER", content, StringComparison.Ordinal);
        Assert.DoesNotContain("MASTHEAD_MARKER", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HtmlSelectsTheDominantArticleOverASmallTeaser()
    {
        const string mainBody =
            "MAIN_ARTICLE_MARKER This is the full guide body. It explains the whole workflow in detail across "
            + "several sentences so that it clearly dominates the short teaser card that precedes it in document "
            + "order, which a first-match selector would otherwise have picked instead.";
        string html = "<html><head><title>Fallback</title></head><body>"
                      + "<article>Short teaser TEASER_MARKER blurb.</article>"
                      + $"<article><h1>Main Guide</h1><p>{mainBody}</p></article>"
                      + "</body></html>";

        DocumentIntakeResult result = await ReadHtmlAsync(html);
        string content = string.Join('\n', result.Sections.Select(section => section.Content));

        Assert.Equal("Main Guide", result.Title);
        Assert.Contains("MAIN_ARTICLE_MARKER", content, StringComparison.Ordinal);
        Assert.DoesNotContain("TEASER_MARKER", content, StringComparison.Ordinal);
    }

    private static async Task<DocumentIntakeResult> ReadHtmlAsync(string html)
    {
        var service = new DocumentIntakeService(Substitute.For<IDoclingClient>());
        return await service.ReadAsync(Request("page.html", "text/html", Encoding.UTF8.GetBytes(html)),
                                       TestContext.Current.CancellationToken);
    }

    private static async Task<string> ReadHtmlContentAsync(string html)
    {
        DocumentIntakeResult result = await ReadHtmlAsync(html);
        Assert.True(result.Succeeded);
        return string.Join('\n', result.Sections.Select(section => section.Content));
    }

    [Theory]
    [InlineData("manual.pdf", "application/pdf")]
    [InlineData("manual.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public async Task PdfAndDocxUseTheDoclingAdapterWithImmutableBytes(string fileName, string mediaType)
    {
        var fixtureName = fileName.EndsWith(".pdf", StringComparison.Ordinal)
            ? "saddlerag-docling-probe.pdf"
            : "saddlerag-docling-probe.docx";
        var bytes = LoadOwnedFixture(fixtureName);
        var mapped = new DoclingMappedDocument(fileName,
                                               "# Manual\r\n\r\nConverted body.",
                                               "Manual\r\nConverted body.",
                                               "{\"status\":\"success\"}",
                                               "{\"document\":{}}",
                                               []);
        var docling = Substitute.For<IDoclingClient>();
        DoclingFile? submitted = null;
        docling.ConvertAsync(Arg.Any<DoclingFile>(), Arg.Any<CancellationToken>())
               .Returns(call =>
                        {
                            submitted = call.Arg<DoclingFile>();
                            return Task.FromResult(DoclingConversionResult.Success(mapped));
                        });
        var service = new DocumentIntakeService(docling);

        var result = await service.ReadAsync(Request(fileName, mediaType, bytes),
                                             TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        await docling.Received(requiredNumberOfCalls: 1)
                     .ConvertAsync(Arg.Any<DoclingFile>(),
                                   Arg.Any<CancellationToken>());
        Assert.NotNull(submitted);
        Assert.Equal(fileName, submitted.FileName);
        Assert.Equal(mediaType, submitted.MediaType);
        Assert.Equal(bytes, submitted.Content.ToArray());
        Assert.False(string.IsNullOrWhiteSpace(result.Provenance?.ConfigurationHash));
        Assert.All(result.Sections, section => Assert.DoesNotContain('\r', section.Content));
    }

    [Theory]
    [InlineData("manual.pdf", "application/pdf")]
    [InlineData("manual.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public async Task CorruptOrMismatchedContainerIsRejectedBeforeDocling(string fileName, string mediaType)
    {
        var docling = Substitute.For<IDoclingClient>();
        var service = new DocumentIntakeService(docling);
        var invalidBytes = fileName.EndsWith(".pdf", StringComparison.Ordinal)
            ? "not a pdf"u8.ToArray()
            : InvalidDocxContainer();

        var result = await service.ReadAsync(Request(fileName, mediaType, invalidBytes),
                                             TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DocumentIntakeReasonCodes.InvalidSignature, result.ReasonCode);
        Assert.Empty(docling.ReceivedCalls());
    }

    [Fact]
    public async Task StructuredPdfBlocksPreserveOrderPageRangeTableAndExactRawResponse()
    {
        const string RawResponse = "{\"document\":{\"name\":\"manual.pdf\"},\"status\":\"success\"}";
        var blocks = new DoclingBlock[]
                         {
                             new(0, DoclingBlockKind.Heading, "section_header", "Safety", 1, 1, null, []),
                             new(1, DoclingBlockKind.Text, "text", "Wear eye protection.", null, 1, null, []),
                             new(2,
                                 DoclingBlockKind.Table,
                                 "table",
                                 string.Empty,
                                 null,
                                 2,
                                 null,
                                 [new DoclingTableCell(0, 1, 0, 1, "Limit", true, false),
                                  new DoclingTableCell(1, 2, 0, 1, "25 psi", false, false)])
                         };
        var mapped = new DoclingMappedDocument("manual.pdf",
                                               "flattened fallback",
                                               "flattened fallback",
                                               RawResponse,
                                               "{\"pages\":[]}",
                                               blocks);
        var docling = SuccessfulDocling(mapped);
        var service = new DocumentIntakeService(docling);

        var result = await service.ReadAsync(Request("manual.pdf",
                                                     "application/pdf",
                                                     LoadOwnedFixture("saddlerag-docling-probe.pdf")),
                                             TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var section = Assert.Single(result.Sections);
        Assert.Equal("Safety", section.Title);
        Assert.Equal(1, section.PageStart);
        Assert.Equal(2, section.PageEnd);
        Assert.True(section.Content.IndexOf("Wear eye protection.", StringComparison.Ordinal)
                    < section.Content.IndexOf("Limit", StringComparison.Ordinal));
        Assert.Contains("25 psi", section.Content, StringComparison.Ordinal);
        Assert.Equal(RawResponse, Encoding.UTF8.GetString(result.ExtractionArtifact.Span));
    }

    [Fact]
    public async Task StructuredDocxHeadingSuppliesDocumentAndSectionTitle()
    {
        const string RawResponse = "{\"document\":{\"name\":\"manual.docx\"}}";
        var mapped = new DoclingMappedDocument("manual.docx",
                                               "fallback",
                                               "fallback",
                                               RawResponse,
                                               "{\"body\":[]}",
                                               [new DoclingBlock(0,
                                                                 DoclingBlockKind.Heading,
                                                                 "title",
                                                                 "Maintenance Manual",
                                                                 1,
                                                                 null,
                                                                 null,
                                                                 []),
                                                new DoclingBlock(1,
                                                                 DoclingBlockKind.Text,
                                                                 "text",
                                                                 "Inspection steps.",
                                                                 null,
                                                                 null,
                                                                 null,
                                                                 [])]);
        var service = new DocumentIntakeService(SuccessfulDocling(mapped));

        var result = await service.ReadAsync(Request(
                                                 "manual.docx",
                                                 "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                                                 LoadOwnedFixture("saddlerag-docling-probe.docx")),
                                             TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("Maintenance Manual", result.Title);
        Assert.Equal("Maintenance Manual", Assert.Single(result.Sections).Title);
        Assert.Equal(RawResponse, Encoding.UTF8.GetString(result.ExtractionArtifact.Span));
    }

    [Fact]
    public async Task DoclingFailurePreservesItsReasonAndActionableDetail()
    {
        var docling = Substitute.For<IDoclingClient>();
        docling.ConvertAsync(Arg.Any<DoclingFile>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromResult(DoclingConversionResult.Failure(
                                                 DoclingReasonCodes.ModelsUnavailable,
                                                 "Docling models are not installed.")));
        var service = new DocumentIntakeService(docling);

        var result = await service.ReadAsync(Request("manual.pdf",
                                                     "application/pdf",
                                                     LoadOwnedFixture("saddlerag-docling-probe.pdf")),
                                             TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.ModelsUnavailable, result.ReasonCode);
        Assert.Equal("Docling models are not installed.", result.Detail);
    }

    [Fact]
    public async Task Utf16BomIsDecodedAndInvalidUtf8IsRejectedWithoutReplacementCharacters()
    {
        var docling = Substitute.For<IDoclingClient>();
        var service = new DocumentIntakeService(docling);
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Readable text")).ToArray();

        var decoded = await service.ReadAsync(Request("notes.txt", "text/plain", utf16),
                                              TestContext.Current.CancellationToken);
        var rejected = await service.ReadAsync(Request("broken.txt", "text/plain", [0xc3, 0x28]),
                                               TestContext.Current.CancellationToken);

        Assert.True(decoded.Succeeded);
        Assert.Contains("Readable text", Assert.Single(decoded.Sections).Content, StringComparison.Ordinal);
        Assert.False(rejected.Succeeded);
        Assert.Equal(DocumentIntakeReasonCodes.UnsupportedEncoding, rejected.ReasonCode);
        Assert.DoesNotContain('\ufffd', string.Join(string.Empty, rejected.Sections.Select(s => s.Content)));
    }

    [Fact]
    public async Task SectionsAreBoundedOrderedAndLineEndingsAreNormalizedWithoutLosingText()
    {
        var docling = Substitute.For<IDoclingClient>();
        var service = new DocumentIntakeService(docling,
                                                new DocumentIntakeLimits { MaxSectionCharacters = 32 });
        var body = new string('x', count: 100);

        var result = await service.ReadAsync(Request("large.md",
                                                     "text/markdown",
                                                     Encoding.UTF8.GetBytes("# Guide\r\n\r\n" + body)),
                                             TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.Sections.Count > 1);
        Assert.Equal(Enumerable.Range(0, result.Sections.Count), result.Sections.Select(s => s.Order));
        Assert.All(result.Sections, section =>
                                           {
                                               Assert.InRange(section.Content.Length, 1, 32);
                                               Assert.DoesNotContain('\r', section.Content);
                                           });
        Assert.Equal(body.Length, result.Sections.Sum(section => section.Content.Count(c => c == 'x')));
    }

    [Fact]
    public async Task EmptyAndUnsupportedDocumentsHaveDistinctReasonsAndDoNotCallDocling()
    {
        var docling = Substitute.For<IDoclingClient>();
        var service = new DocumentIntakeService(docling);

        var empty = await service.ReadAsync(Request("empty.txt", "text/plain", " \r\n "u8.ToArray()),
                                            TestContext.Current.CancellationToken);
        var unsupported = await service.ReadAsync(Request("legacy.rtf", "application/rtf", "rtf"u8.ToArray()),
                                                  TestContext.Current.CancellationToken);

        Assert.Equal(DocumentIntakeReasonCodes.EmptyContent, empty.ReasonCode);
        Assert.Equal(DocumentIntakeReasonCodes.UnsupportedFormat, unsupported.ReasonCode);
        Assert.Empty(docling.ReceivedCalls());
    }

    private static DocumentIntakeRequest Request(string fileName, string mediaType, byte[] content) =>
        new(fileName, fileName, mediaType, content);

    private static IDoclingClient SuccessfulDocling(DoclingMappedDocument document)
    {
        var result = Substitute.For<IDoclingClient>();
        result.ConvertAsync(Arg.Any<DoclingFile>(), Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult(DoclingConversionResult.Success(document)));
        return result;
    }

    private static byte[] LoadOwnedFixture(string fileName)
    {
        var projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var path = Path.Combine(projectDirectory, "TestData", "Documents", fileName);
        return File.ReadAllBytes(path);
    }

    private static byte[] InvalidDocxContainer()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("[Content_Types].xml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("<Types />");
        }

        return stream.ToArray();
    }
}
