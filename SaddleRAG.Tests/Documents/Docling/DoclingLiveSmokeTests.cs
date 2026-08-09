// DoclingLiveSmokeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Ingestion.Documents.Docling;

#endregion

namespace SaddleRAG.Tests.Documents.Docling;

/// <summary>
///     Explicitly gated boundary smoke against a user-managed Docling endpoint.
///     Ordinary test runs always skip it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DoclingLiveSmokeTests
{
    [Fact]
    public async Task OwnedPdfAndDocxConvertWithKnownMarker()
    {
        var enabled = string.Equals(Environment.GetEnvironmentVariable(OptInVariable),
                                    EnabledValue,
                                    StringComparison.Ordinal);
        Assert.SkipUnless(enabled, OptInSkipReason);

        var settings = new DoclingSettings
                       {
                           Endpoint = Environment.GetEnvironmentVariable(EndpointVariable)
                                      ?? DoclingSettings.DefaultEndpoint,
                           ApiKey = Environment.GetEnvironmentVariable(ApiKeyVariable) ?? string.Empty
                       };
        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var client = new DoclingClient(httpClient, settings, new DoclingDocumentMapper());

        var health = await client.CheckHealthAsync(TestContext.Current.CancellationToken);
        var readiness = await client.CheckReadinessAsync(TestContext.Current.CancellationToken);
        Assert.True(health.Succeeded, health.Detail);
        Assert.True(readiness.Succeeded, readiness.Detail);

        var documents = new[]
                        {
                            LoadFile(PdfFileName, PdfMediaType),
                            LoadFile(DocxFileName, DocxMediaType)
                        };
        foreach(var document in documents)
        {
            var result = await client.ConvertAsync(document, TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, $"{document.FileName}: {result.ReasonCode} {result.Detail}");
            var mapped = Assert.IsType<DoclingMappedDocument>(result.Document);
            var markerPresent = DoclingProbeDocument.ContainsMarker(mapped);
            Assert.True(markerPresent, $"{document.FileName}: known marker was not returned");
        }
    }

    private static DoclingFile LoadFile(string fileName, string mediaType)
    {
        var path = Path.Combine(DoclingTestSupport.RepositoryRoot(),
                                "SaddleRAG.Tests",
                                "TestData",
                                "Documents",
                                fileName);
        return new DoclingFile(fileName, mediaType, File.ReadAllBytes(path));
    }

    internal const string OptInVariable = "SADDLERAG_RUN_DOCLING_SMOKE";
    internal const string EndpointVariable = "SADDLERAG_DOCLING_ENDPOINT";
    internal const string ApiKeyVariable = "SADDLERAG_DOCLING_API_KEY";
    internal const string EnabledValue = "1";
    private const string OptInSkipReason =
        "Set SADDLERAG_RUN_DOCLING_SMOKE=1 only for the explicitly approved one-time Docling boundary smoke.";
    private const string PdfFileName = "saddlerag-docling-probe.pdf";
    private const string DocxFileName = "saddlerag-docling-probe.docx";
    private const string PdfMediaType = "application/pdf";
    private const string DocxMediaType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
}
