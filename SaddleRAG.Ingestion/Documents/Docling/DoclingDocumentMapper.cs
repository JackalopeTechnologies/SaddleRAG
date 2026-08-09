// DoclingDocumentMapper.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json;

#endregion

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>
///     Maps Docling's lossless document JSON to the stable subset SaddleRAG consumes.
/// </summary>
public sealed class DoclingDocumentMapper
{
    public DoclingConversionResult Map(string responseJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(responseJson);
        DoclingConversionResult result;
        try
        {
            using var responseDocument = JsonDocument.Parse(responseJson);
            var root = responseDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(StatusProperty, out var statusElement)
                || statusElement.ValueKind != JsonValueKind.String)
            {
                result = DoclingConversionResult.Failure(DoclingReasonCodes.ApiIncompatible,
                                                         IncompatibleResponseDetail,
                                                         responseJson);
            }
            else
            {
                var status = statusElement.GetString() ?? string.Empty;
                result = status switch
                {
                    SuccessStatus => MapSuccessfulResponse(root, responseJson),
                    PartialSuccessStatus => DoclingConversionResult.Failure(
                        DoclingReasonCodes.PartialConversion,
                        ReadErrors(root, PartialConversionDetail),
                        responseJson),
                    FailureStatus or SkippedStatus => DoclingConversionResult.Failure(
                        DoclingReasonCodes.ConversionFailed,
                        ReadErrors(root, ConversionFailureDetail),
                        responseJson),
                    _ => DoclingConversionResult.Failure(DoclingReasonCodes.ApiIncompatible,
                                                         UnknownStatusDetail,
                                                         responseJson)
                };
            }
        }
        catch(JsonException ex)
        {
            result = DoclingConversionResult.Failure(DoclingReasonCodes.OutputInvalid,
                                                     $"{MalformedResponsePrefix} {ex.Message}",
                                                     responseJson);
        }

        return result;
    }

    private static DoclingConversionResult MapSuccessfulResponse(JsonElement root, string responseJson)
    {
        DoclingConversionResult result;
        if (!root.TryGetProperty(DocumentProperty, out var exportedDocument)
            || exportedDocument.ValueKind != JsonValueKind.Object)
        {
            result = DoclingConversionResult.Failure(DoclingReasonCodes.ApiIncompatible,
                                                     MissingDocumentDetail,
                                                     responseJson);
        }
        else
        {
            var fileName = ReadString(exportedDocument, FileNameProperty);
            var markdown = ReadString(exportedDocument, MarkdownContentProperty);
            var text = ReadString(exportedDocument, TextContentProperty);
            var hasReadableArtifact = !string.IsNullOrEmpty(markdown) || !string.IsNullOrEmpty(text);
            var hasJsonArtifact = exportedDocument.TryGetProperty(JsonContentProperty, out var jsonContent)
                                  && (jsonContent.ValueKind == JsonValueKind.Object
                                      || jsonContent.ValueKind == JsonValueKind.String);
            if (!hasReadableArtifact || !hasJsonArtifact)
            {
                result = DoclingConversionResult.Failure(DoclingReasonCodes.ArtifactsUnavailable,
                                                         MissingArtifactsDetail,
                                                         responseJson);
            }
            else
            {
                result = MapLosslessDocument(jsonContent, fileName, markdown, text, responseJson);
            }
        }

        return result;
    }

    private static DoclingConversionResult MapLosslessDocument(JsonElement jsonContent,
                                                               string fileName,
                                                               string markdown,
                                                               string text,
                                                               string responseJson)
    {
        DoclingConversionResult result;
        try
        {
            if (jsonContent.ValueKind == JsonValueKind.Object)
            {
                result = BuildMappedDocument(jsonContent,
                                             jsonContent.GetRawText(),
                                             fileName,
                                             markdown,
                                             text,
                                             responseJson);
            }
            else
            {
                var rawDocumentJson = jsonContent.GetString() ?? string.Empty;
                using var nestedDocument = JsonDocument.Parse(rawDocumentJson);
                result = BuildMappedDocument(nestedDocument.RootElement,
                                             rawDocumentJson,
                                             fileName,
                                             markdown,
                                             text,
                                             responseJson);
            }
        }
        catch(JsonException ex)
        {
            result = DoclingConversionResult.Failure(DoclingReasonCodes.OutputInvalid,
                                                     $"{MalformedDocumentPrefix} {ex.Message}",
                                                     responseJson);
        }

        return result;
    }

    private static DoclingConversionResult BuildMappedDocument(JsonElement doclingDocument,
                                                               string rawDocumentJson,
                                                               string fileName,
                                                               string markdown,
                                                               string text,
                                                               string responseJson)
    {
        DoclingConversionResult result;
        if (doclingDocument.ValueKind != JsonValueKind.Object)
        {
            result = DoclingConversionResult.Failure(DoclingReasonCodes.OutputInvalid,
                                                     LosslessObjectDetail,
                                                     responseJson);
        }
        else
        {
            var blocks = MapBlocks(doclingDocument);
            var mapped = new DoclingMappedDocument(fileName,
                                                   markdown,
                                                   text,
                                                   responseJson,
                                                   rawDocumentJson,
                                                   blocks);
            result = DoclingConversionResult.Success(mapped);
        }

        return result;
    }

    private static IReadOnlyList<DoclingBlock> MapBlocks(JsonElement document)
    {
        var items = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var groups = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        IndexArray(document, TextsProperty, TextReferencePrefix, items);
        IndexArray(document, TablesProperty, TableReferencePrefix, items);
        IndexArray(document, GroupsProperty, GroupReferencePrefix, groups);

        var blocks = new List<DoclingBlock>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (document.TryGetProperty(BodyProperty, out var body) && body.ValueKind == JsonValueKind.Object)
            AppendChildren(body, items, groups, visited, blocks);

        if (blocks.Count == 0)
        {
            AppendArrayFallback(document, TextsProperty, blocks);
            AppendArrayFallback(document, TablesProperty, blocks);
        }

        return blocks;
    }

    private static void IndexArray(JsonElement document,
                                   string propertyName,
                                   string referencePrefix,
                                   IDictionary<string, JsonElement> destination)
    {
        if (document.TryGetProperty(propertyName, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach(var item in array.EnumerateArray())
            {
                IndexItem(item, referencePrefix, index, destination);
                index++;
            }
        }
    }

    private static void IndexItem(JsonElement item,
                                  string referencePrefix,
                                  int index,
                                  IDictionary<string, JsonElement> destination)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            var reference = ReadString(item, SelfReferenceProperty);
            if (string.IsNullOrEmpty(reference))
                reference = $"{referencePrefix}{index}";
            destination[reference] = item;
        }
    }

    private static void AppendChildren(JsonElement container,
                                       IReadOnlyDictionary<string, JsonElement> items,
                                       IReadOnlyDictionary<string, JsonElement> groups,
                                       ISet<string> visited,
                                       ICollection<DoclingBlock> blocks)
    {
        if (container.TryGetProperty(ChildrenProperty, out var children)
            && children.ValueKind == JsonValueKind.Array)
        {
            foreach(var child in children.EnumerateArray())
                AppendChild(child, items, groups, visited, blocks);
        }
    }

    private static void AppendChild(JsonElement child,
                                    IReadOnlyDictionary<string, JsonElement> items,
                                    IReadOnlyDictionary<string, JsonElement> groups,
                                    ISet<string> visited,
                                    ICollection<DoclingBlock> blocks)
    {
        var reference = ReadReference(child);
        if (!string.IsNullOrEmpty(reference) && visited.Add(reference))
        {
            if (groups.TryGetValue(reference, out var group))
                AppendChildren(group, items, groups, visited, blocks);
            if (items.TryGetValue(reference, out var item))
            {
                blocks.Add(MapBlock(item, reference, blocks.Count));
                AppendChildren(item, items, groups, visited, blocks);
            }
        }
    }

    private static void AppendArrayFallback(JsonElement document,
                                            string propertyName,
                                            ICollection<DoclingBlock> blocks)
    {
        if (document.TryGetProperty(propertyName, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach(var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    blocks.Add(MapBlock(item, ReadString(item, SelfReferenceProperty), blocks.Count));
            }
        }
    }

    private static DoclingBlock MapBlock(JsonElement item, string reference, int order)
    {
        var label = ReadString(item, LabelProperty);
        var isTable = label.Equals(TableLabel, StringComparison.OrdinalIgnoreCase)
                      || reference.StartsWith(TableReferencePrefix, StringComparison.Ordinal);
        var isHeading = label.Equals(TitleLabel, StringComparison.OrdinalIgnoreCase)
                        || label.Equals(SectionHeaderLabel, StringComparison.OrdinalIgnoreCase)
                        || label.Equals(SubtitleLabel, StringComparison.OrdinalIgnoreCase);
        var kind = isTable
            ? DoclingBlockKind.Table
            : isHeading
                ? DoclingBlockKind.Heading
                : DoclingBlockKind.Text;
        var tableCells = isTable ? ReadTableCells(item) : [];
        var blockText = isTable ? FormatTable(tableCells) : ReadString(item, TextProperty);
        var headingLevel = isHeading ? ReadHeadingLevel(item, label) : null;
        var pageNumber = ReadPageNumber(item);
        var boundingBox = ReadBoundingBox(item);
        return new DoclingBlock(order,
                                kind,
                                label,
                                blockText,
                                headingLevel,
                                pageNumber,
                                boundingBox,
                                tableCells);
    }

    private static IReadOnlyList<DoclingTableCell> ReadTableCells(JsonElement item)
    {
        var cells = new List<DoclingTableCell>();
        if (item.TryGetProperty(DataProperty, out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(TableCellsProperty, out var tableCells)
            && tableCells.ValueKind == JsonValueKind.Array)
        {
            foreach(var cell in tableCells.EnumerateArray())
            {
                if (cell.ValueKind == JsonValueKind.Object)
                {
                    cells.Add(new DoclingTableCell(ReadInt32(cell, StartRowProperty),
                                                   ReadInt32(cell, EndRowProperty),
                                                   ReadInt32(cell, StartColumnProperty),
                                                   ReadInt32(cell, EndColumnProperty),
                                                   ReadString(cell, TextProperty),
                                                   ReadBoolean(cell, ColumnHeaderProperty),
                                                   ReadBoolean(cell, RowHeaderProperty)));
                }
            }
        }

        return cells;
    }

    private static string FormatTable(IReadOnlyList<DoclingTableCell> cells)
    {
        var rows = cells.GroupBy(cell => cell.StartRow)
                        .OrderBy(row => row.Key)
                        .Select(row => string.Join(TableCellSeparator,
                                                   row.OrderBy(cell => cell.StartColumn)
                                                      .Select(cell => cell.Text)));
        return string.Join(Environment.NewLine, rows);
    }

    private static int? ReadHeadingLevel(JsonElement item, string label)
    {
        int? result;
        if (item.TryGetProperty(LevelProperty, out var level) && level.TryGetInt32(out var parsedLevel))
            result = parsedLevel;
        else
            result = label.Equals(TitleLabel, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        return result;
    }

    private static int? ReadPageNumber(JsonElement item)
    {
        int? result = null;
        if (TryGetFirstProvenance(item, out var provenance)
            && provenance.TryGetProperty(PageNumberProperty, out var pageNumber)
            && pageNumber.TryGetInt32(out var parsedPageNumber))
        {
            result = parsedPageNumber;
        }

        return result;
    }

    private static DoclingBoundingBox? ReadBoundingBox(JsonElement item)
    {
        DoclingBoundingBox? result = null;
        if (TryGetFirstProvenance(item, out var provenance)
            && provenance.TryGetProperty(BoundingBoxProperty, out var boundingBox)
            && boundingBox.ValueKind == JsonValueKind.Object)
        {
            result = new DoclingBoundingBox(ReadDouble(boundingBox, LeftProperty),
                                            ReadDouble(boundingBox, TopProperty),
                                            ReadDouble(boundingBox, RightProperty),
                                            ReadDouble(boundingBox, BottomProperty),
                                            ReadString(boundingBox, CoordinateOriginProperty));
        }

        return result;
    }

    private static bool TryGetFirstProvenance(JsonElement item, out JsonElement provenance)
    {
        var result = false;
        provenance = default;
        if (item.TryGetProperty(ProvenanceProperty, out var provenanceArray)
            && provenanceArray.ValueKind == JsonValueKind.Array)
        {
            var enumerator = provenanceArray.EnumerateArray();
            if (enumerator.MoveNext() && enumerator.Current.ValueKind == JsonValueKind.Object)
            {
                provenance = enumerator.Current;
                result = true;
            }
        }

        return result;
    }

    private static string ReadReference(JsonElement element)
    {
        var result = string.Empty;
        if (element.ValueKind == JsonValueKind.Object)
            result = ReadString(element, ReferenceProperty);
        return result;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        var result = string.Empty;
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            result = property.GetString() ?? string.Empty;
        }

        return result;
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        var result = 0;
        if (element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value))
            result = value;
        return result;
    }

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        var result = 0.0;
        if (element.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value))
            result = value;
        return result;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        var result = false;
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            result = property.GetBoolean();
        }

        return result;
    }

    private static string ReadErrors(JsonElement root, string fallback)
    {
        var messages = new List<string>();
        if (root.TryGetProperty(ErrorsProperty, out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            foreach(var error in errors.EnumerateArray())
            {
                var message = error.ValueKind == JsonValueKind.String
                    ? error.GetString() ?? string.Empty
                    : error.GetRawText();
                if (!string.IsNullOrWhiteSpace(message))
                    messages.Add(message);
            }
        }

        var result = messages.Count == 0 ? fallback : string.Join(ErrorSeparator, messages);
        return result;
    }

    private const string StatusProperty = "status";
    private const string DocumentProperty = "document";
    private const string FileNameProperty = "filename";
    private const string MarkdownContentProperty = "md_content";
    private const string TextContentProperty = "text_content";
    private const string JsonContentProperty = "json_content";
    private const string ErrorsProperty = "errors";
    private const string BodyProperty = "body";
    private const string ChildrenProperty = "children";
    private const string TextsProperty = "texts";
    private const string TablesProperty = "tables";
    private const string GroupsProperty = "groups";
    private const string SelfReferenceProperty = "self_ref";
    private const string ReferenceProperty = "$ref";
    private const string LabelProperty = "label";
    private const string TextProperty = "text";
    private const string LevelProperty = "level";
    private const string ProvenanceProperty = "prov";
    private const string PageNumberProperty = "page_no";
    private const string BoundingBoxProperty = "bbox";
    private const string LeftProperty = "l";
    private const string TopProperty = "t";
    private const string RightProperty = "r";
    private const string BottomProperty = "b";
    private const string CoordinateOriginProperty = "coord_origin";
    private const string DataProperty = "data";
    private const string TableCellsProperty = "table_cells";
    private const string StartRowProperty = "start_row_offset_idx";
    private const string EndRowProperty = "end_row_offset_idx";
    private const string StartColumnProperty = "start_col_offset_idx";
    private const string EndColumnProperty = "end_col_offset_idx";
    private const string ColumnHeaderProperty = "column_header";
    private const string RowHeaderProperty = "row_header";
    private const string SuccessStatus = "success";
    private const string PartialSuccessStatus = "partial_success";
    private const string FailureStatus = "failure";
    private const string SkippedStatus = "skipped";
    private const string TitleLabel = "title";
    private const string SectionHeaderLabel = "section_header";
    private const string SubtitleLabel = "subtitle";
    private const string TableLabel = "table";
    private const string TextReferencePrefix = "#/texts/";
    private const string TableReferencePrefix = "#/tables/";
    private const string GroupReferencePrefix = "#/groups/";
    private const string TableCellSeparator = " | ";
    private const string ErrorSeparator = "; ";
    private const string IncompatibleResponseDetail = "Docling returned a response that does not match the v1 contract.";
    private const string MissingDocumentDetail = "Docling v1 returned success without a document object.";
    private const string MissingArtifactsDetail = "Docling did not return both lossless JSON and readable text artifacts.";
    private const string PartialConversionDetail = "Docling reported a partial conversion.";
    private const string ConversionFailureDetail = "Docling reported a conversion failure.";
    private const string UnknownStatusDetail = "Docling returned an unknown v1 conversion status.";
    private const string MalformedResponsePrefix = "Docling returned malformed response JSON:";
    private const string MalformedDocumentPrefix = "Docling returned malformed lossless document JSON:";
    private const string LosslessObjectDetail = "Docling lossless JSON is not a document object.";
}
