// DocumentContentTypeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.
// Stage 8 acceptance contract.

using SaddleRAG.Ingestion.Crawling;

namespace SaddleRAG.Tests.Crawling;

/// <summary>
///     Stage 8 RED draft. Rename to .cs only when Stage 8 begins. These tests
///     pin the pure response classifier used before any DOM/SPA preparation.
/// </summary>
public sealed class DocumentContentTypeTests
{
    [Fact]
    public void PdfUsesResponseMetadataAndSignature()
    {
        FetchedWebResponse response = Response("https://owned.test/manual.pdf",
                                               "application/pdf",
                                               PdfBytes);

        DocumentResponseKind result = DocumentResponseClassifier.Classify(response);

        Assert.Equal(DocumentResponseKind.Pdf, result);
    }

    [Fact]
    public void DocxUsesOoxmlMetadataAndZipSignature()
    {
        FetchedWebResponse response = Response(
            "https://owned.test/manual.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            DocxBytes);

        DocumentResponseKind result = DocumentResponseClassifier.Classify(response);

        Assert.Equal(DocumentResponseKind.Docx, result);
    }

    [Fact]
    public void ExtensionlessPdfRoutesFromMetadataAndSignature()
    {
        FetchedWebResponse response = Response("https://owned.test/download?id=17",
                                               "application/pdf; charset=binary",
                                               PdfBytes);

        DocumentResponseKind result = DocumentResponseClassifier.Classify(response);

        Assert.Equal(DocumentResponseKind.Pdf, result);
    }

    [Fact]
    public void HtmlResponseWinsOverMisleadingPdfSuffix()
    {
        FetchedWebResponse response = Response("https://owned.test/not-a-document.pdf",
                                               "text/html; charset=utf-8",
                                               HtmlBytes);

        DocumentResponseKind result = DocumentResponseClassifier.Classify(response);

        Assert.Equal(DocumentResponseKind.Html, result);
    }

    [Fact]
    public void PdfSuffixAloneCannotTurnArbitraryBytesIntoADocument()
    {
        FetchedWebResponse response = Response("https://owned.test/not-a-document.pdf",
                                               "application/octet-stream",
                                               "plain text, not a PDF"u8.ToArray());

        DocumentResponseKind result = DocumentResponseClassifier.Classify(response);

        Assert.Equal(DocumentResponseKind.Other, result);
    }

    [Fact]
    public void ClassifierDoesNotMutateTheExactAcquiredBytesOrProvenance()
    {
        byte[] bytes = PdfBytes.ToArray();
        var response = new FetchedWebResponse(OriginalUrl: "https://owned.test/link",
                                              AttemptedUrl: "https://owned.test/link",
                                              FinalUrl: "https://owned.test/manual.pdf",
                                              StatusCode: 200,
                                              Headers: Headers("application/pdf"),
                                              Body: bytes);

        DocumentResponseKind result = DocumentResponseClassifier.Classify(response);

        Assert.Equal(DocumentResponseKind.Pdf, result);
        Assert.Equal("https://owned.test/link", response.OriginalUrl);
        Assert.Equal("https://owned.test/link", response.AttemptedUrl);
        Assert.Equal("https://owned.test/manual.pdf", response.FinalUrl);
        Assert.Equal(bytes, response.Body.ToArray());
    }

    [Fact]
    public void ResponseSnapshotCopiesMutableHeadersAndBodyAtAcquisitionBoundary()
    {
        byte[] sourceBody = PdfBytes.ToArray();
        var sourceHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["content-type"] = "application/pdf"
                                };
        var response = new FetchedWebResponse(OriginalUrl: "https://owned.test/manual.pdf",
                                              AttemptedUrl: "https://owned.test/manual.pdf",
                                              FinalUrl: "https://owned.test/manual.pdf",
                                              StatusCode: 200,
                                              Headers: sourceHeaders,
                                              Body: sourceBody);

        sourceBody[0] = 0x00;
        sourceHeaders["content-type"] = "text/html";

        Assert.Equal(0x25, response.Body.Span[0]);
        Assert.Equal("application/pdf", response.Headers["content-type"]);
        Assert.Equal(DocumentResponseKind.Pdf, DocumentResponseClassifier.Classify(response));
    }

    private static FetchedWebResponse Response(string url, string contentType, byte[] body) =>
        new(OriginalUrl: url,
            AttemptedUrl: url,
            FinalUrl: url,
            StatusCode: 200,
            Headers: Headers(contentType),
            Body: body);

    private static IReadOnlyDictionary<string, string> Headers(string contentType) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = contentType
            };

    private static readonly byte[] PdfBytes =
        [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x0A, 0x00, 0xFF, 0x0A];

    private static readonly byte[] DocxBytes =
        [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00];

    private static readonly byte[] HtmlBytes =
        "<!doctype html><html><body>Owned HTML</body></html>"u8.ToArray();
}
