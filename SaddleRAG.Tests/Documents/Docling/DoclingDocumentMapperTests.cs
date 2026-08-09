// DoclingDocumentMapperTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json.Nodes;
using SaddleRAG.Ingestion.Documents.Docling;

#endregion

namespace SaddleRAG.Tests.Documents.Docling;

public sealed class DoclingDocumentMapperTests
{
    [Fact]
    public void PdfResponsePreservesOrderedBlocksPagesTablesAndBoundingBoxes()
    {
        var json = DoclingTestSupport.LoadFixture("docling-v1-pdf-success.json");
        var mapper = new DoclingDocumentMapper();

        var result = mapper.Map(json);

        Assert.True(result.Succeeded);
        var document = Assert.IsType<DoclingMappedDocument>(result.Document);
        Assert.Equal("saddlerag-docling-probe.pdf", document.FileName);
        Assert.Equal(json, document.RawResponseJson);
        Assert.Contains("unknown_future_document_property", document.RawDocumentJson, StringComparison.Ordinal);
        Assert.Contains(DoclingProbeDocument.Marker, document.MarkdownContent, StringComparison.Ordinal);

        Assert.Collection(document.Blocks,
                          title =>
                          {
                              Assert.Equal(expected: 0, title.Order);
                              Assert.Equal(DoclingBlockKind.Heading, title.Kind);
                              Assert.Equal(expected: 1, title.HeadingLevel);
                              Assert.Equal(expected: 1, title.PageNumber);
                              Assert.Equal(expected: 72.0, Assert.IsType<DoclingBoundingBox>(title.BoundingBox).Left);
                          },
                          section =>
                          {
                              Assert.Equal(expected: 1, section.Order);
                              Assert.Equal(DoclingBlockKind.Heading, section.Kind);
                              Assert.Equal("Purpose", section.Text);
                              Assert.Equal(expected: 2, section.HeadingLevel);
                          },
                          paragraph =>
                          {
                              Assert.Equal(expected: 2, paragraph.Order);
                              Assert.Equal(DoclingBlockKind.Text, paragraph.Kind);
                              Assert.Contains(DoclingProbeDocument.Marker, paragraph.Text, StringComparison.Ordinal);
                              Assert.Equal(expected: 1, paragraph.PageNumber);
                          },
                          table =>
                          {
                              Assert.Equal(expected: 3, table.Order);
                              Assert.Equal(DoclingBlockKind.Table, table.Kind);
                              Assert.Equal(expected: 1, table.PageNumber);
                              Assert.Equal(expected: 4, table.TableCells.Count);
                              Assert.Equal("Capability", table.TableCells[index: 0].Text);
                              Assert.True(table.TableCells[index: 0].IsColumnHeader);
                              Assert.Contains("PDF", table.Text, StringComparison.Ordinal);
                              Assert.NotNull(table.BoundingBox);
                          });
    }

    [Fact]
    public void DocxResponsePreservesHeadingHierarchyWithoutInventingPages()
    {
        var json = DoclingTestSupport.LoadFixture("docling-v1-docx-success.json");

        var result = new DoclingDocumentMapper().Map(json);

        Assert.True(result.Succeeded);
        var blocks = Assert.IsType<DoclingMappedDocument>(result.Document).Blocks;
        Assert.Equal(expected: 3, blocks.Count);
        Assert.Equal(new int?[] { 1, 2, null }, blocks.Select(block => block.HeadingLevel).ToArray());
        Assert.All(blocks, block => Assert.Null(block.PageNumber));
        Assert.Equal(["SaddleRAG Docling Probe", "Verification",
                      "SADDLERAG_DOCLING_PROBE_2026_08_04 This document verifies local DOCX conversion."],
                     blocks.Select(block => block.Text).ToArray());
    }

    [Fact]
    public void NestedDocxTextChildrenAreTraversedInDocumentOrder()
    {
        JsonObject root = JsonNode.Parse(DoclingTestSupport.LoadFixture("docling-v1-docx-success.json"))?.AsObject()
                          ?? throw new InvalidOperationException("Fixture is invalid.");
        JsonObject document = root["document"]?.AsObject()
                              ?? throw new InvalidOperationException("Fixture document is invalid.");
        JsonObject jsonContent = document["json_content"]?.AsObject()
                                 ?? throw new InvalidOperationException("Fixture JSON content is invalid.");
        JsonArray texts = jsonContent["texts"]?.AsArray()
                          ?? throw new InvalidOperationException("Fixture texts are invalid.");
        JsonObject body = jsonContent["body"]?.AsObject()
                          ?? throw new InvalidOperationException("Fixture body is invalid.");
        body["children"] = new JsonArray
                               {
                                   new JsonObject { ["$ref"] = "#/groups/0" }
                               };
        jsonContent["groups"] = new JsonArray
                                    {
                                        new JsonObject
                                            {
                                                ["self_ref"] = "#/groups/0",
                                                ["children"] = new JsonArray
                                                                   {
                                                                       new JsonObject
                                                                           {
                                                                               ["$ref"] = "#/texts/0"
                                                                           }
                                                                   },
                                                ["label"] = "section"
                                            }
                                    };
        texts[index: 0]?.AsObject()["children"] = new JsonArray
                                                       {
                                                           new JsonObject { ["$ref"] = "#/texts/1" }
                                                       };
        texts[index: 1]?.AsObject()["children"] = new JsonArray
                                                       {
                                                           new JsonObject { ["$ref"] = "#/texts/2" }
                                                       };

        DoclingConversionResult result = new DoclingDocumentMapper().Map(root.ToJsonString());

        Assert.True(result.Succeeded);
        IReadOnlyList<DoclingBlock> blocks = Assert.IsType<DoclingMappedDocument>(result.Document).Blocks;
        Assert.Equal(expected: 3, blocks.Count);
        Assert.Equal(["SaddleRAG Docling Probe", "Verification",
                      "SADDLERAG_DOCLING_PROBE_2026_08_04 This document verifies local DOCX conversion."],
                     blocks.Select(block => block.Text).ToArray());
        Assert.Contains(DoclingProbeDocument.Marker, blocks[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownForwardCompatibleFieldsDoNotChangeMappedOrder()
    {
        var json = DoclingTestSupport.LoadFixture("docling-v1-pdf-success.json");

        var result = new DoclingDocumentMapper().Map(json);

        Assert.True(result.Succeeded);
        var document = Assert.IsType<DoclingMappedDocument>(result.Document);
        Assert.Equal(expected: 4, document.Blocks.Count);
        Assert.Contains("unknown_future_response_property", document.RawResponseJson, StringComparison.Ordinal);
        Assert.Contains("unknown_future_text_property", document.RawDocumentJson, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedJsonIsOutputInvalid()
    {
        var result = new DoclingDocumentMapper().Map("{ definitely not json");

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.OutputInvalid, result.ReasonCode);
    }

    [Fact]
    public void WellFormedButIncompatibleResponseIsApiIncompatible()
    {
        var result = new DoclingDocumentMapper().Map("{\"unexpected\":true}");

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.ApiIncompatible, result.ReasonCode);
    }

    [Fact]
    public void MissingLosslessJsonArtifactIsDistinct()
    {
        var root = JsonNode.Parse(DoclingTestSupport.LoadFixture("docling-v1-pdf-success.json"))?.AsObject()
                   ?? throw new InvalidOperationException("Fixture is invalid.");
        root["document"]?.AsObject().Remove("json_content");

        var result = new DoclingDocumentMapper().Map(root.ToJsonString());

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.ArtifactsUnavailable, result.ReasonCode);
    }

    [Fact]
    public void MissingReadableTextArtifactsIsDistinct()
    {
        var root = JsonNode.Parse(DoclingTestSupport.LoadFixture("docling-v1-pdf-success.json"))?.AsObject()
                   ?? throw new InvalidOperationException("Fixture is invalid.");
        var document = root["document"]?.AsObject()
                       ?? throw new InvalidOperationException("Fixture document is invalid.");
        document.Remove("md_content");
        document.Remove("text_content");

        var result = new DoclingDocumentMapper().Map(root.ToJsonString());

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.ArtifactsUnavailable, result.ReasonCode);
    }

    [Fact]
    public void PartialSuccessIsNeverFlattenedToReady()
    {
        var root = JsonNode.Parse(DoclingTestSupport.LoadFixture("docling-v1-pdf-success.json"))?.AsObject()
                   ?? throw new InvalidOperationException("Fixture is invalid.");
        root["status"] = "partial_success";
        root["errors"] = new JsonArray("One page could not be parsed");

        var result = new DoclingDocumentMapper().Map(root.ToJsonString());

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.PartialConversion, result.ReasonCode);
        Assert.Contains("One page", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureStatusRetainsRecordedErrors()
    {
        var root = JsonNode.Parse(DoclingTestSupport.LoadFixture("docling-v1-pdf-success.json"))?.AsObject()
                   ?? throw new InvalidOperationException("Fixture is invalid.");
        root["status"] = "failure";
        root["errors"] = new JsonArray("OCR model unavailable");

        var result = new DoclingDocumentMapper().Map(root.ToJsonString());

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.ConversionFailed, result.ReasonCode);
        Assert.Contains("OCR model unavailable", result.Detail, StringComparison.Ordinal);
    }
}
