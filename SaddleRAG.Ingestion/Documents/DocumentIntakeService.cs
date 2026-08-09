// DocumentIntakeService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Documents.Docling;

namespace SaddleRAG.Ingestion.Documents.Intake;

/// <summary>Routes local text formats directly and PDF/DOCX through Docling.</summary>
public sealed class DocumentIntakeService : IDocumentIntake, IDocumentIntakeFingerprintProvider
{
    public DocumentIntakeService(IDoclingClient doclingClient, DocumentIntakeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(doclingClient);
        mDoclingClient = doclingClient;
        mLimits = limits ?? new DocumentIntakeLimits();
        if (mLimits.MaxSectionCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "The section-size bound must be positive.");
    }

    private readonly IDoclingClient mDoclingClient;
    private readonly DocumentIntakeLimits mLimits;

    public async Task<DocumentIntakeResult> ReadAsync(DocumentIntakeRequest request,
                                                      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.FileName);
        ArgumentException.ThrowIfNullOrEmpty(request.RelativePath);
        ArgumentException.ThrowIfNullOrEmpty(request.MediaType);
        cancellationToken.ThrowIfCancellationRequested();

        var extension = Path.GetExtension(request.FileName).ToLowerInvariant();
        DocumentExtractionFingerprint fingerprint = CreateFingerprint(request.FileName, request.MediaType);
        DocumentIntakeResult result;
        switch(extension)
        {
            case ".md":
            case ".markdown":
                result = ReadLocalText(request, fingerprint, isMarkdown: true, isHtml: false);
                break;
            case ".txt":
            case ".text":
                result = ReadLocalText(request, fingerprint, isMarkdown: false, isHtml: false);
                break;
            case ".html":
            case ".htm":
                result = ReadLocalText(request, fingerprint, isMarkdown: false, isHtml: true);
                break;
            case ".pdf":
                result = HasPdfSignature(request.Content)
                    ? await ReadWithDoclingAsync(request, fingerprint, cancellationToken)
                    : Failure(DocumentIntakeReasonCodes.InvalidSignature, InvalidSignatureDetail);
                break;
            case ".docx":
                result = HasDocxSignature(request.Content)
                    ? await ReadWithDoclingAsync(request, fingerprint, cancellationToken)
                    : Failure(DocumentIntakeReasonCodes.InvalidSignature, InvalidSignatureDetail);
                break;
            default:
                result = Failure(DocumentIntakeReasonCodes.UnsupportedFormat, UnsupportedFormatDetail);
                break;
        }

        return result;
    }

    private DocumentIntakeResult ReadLocalText(DocumentIntakeRequest request,
                                               DocumentExtractionFingerprint fingerprint,
                                               bool isMarkdown,
                                               bool isHtml)
    {
        DocumentIntakeResult result;
        if (!TryDecode(request.Content, out var decoded))
            result = Failure(DocumentIntakeReasonCodes.UnsupportedEncoding, UnsupportedEncodingDetail);
        else
        {
            var normalized = NormalizeLineEndings(decoded).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                result = Failure(DocumentIntakeReasonCodes.EmptyContent, EmptyContentDetail);
            else
            {
                if (isHtml)
                    result = ReadHtml(request.FileName, normalized, fingerprint);
                else
                {
                    var title = isMarkdown
                        ? FindMarkdownTitle(normalized, request.FileName)
                        : Path.GetFileNameWithoutExtension(request.FileName);
                    result = Success(title,
                                     CreateBoundedSections(title, normalized, null, null),
                                     fingerprint,
                                     rawArtifact: null);
                }
            }
        }

        return result;
    }

    private DocumentIntakeResult ReadHtml(string fileName,
                                          string html,
                                          DocumentExtractionFingerprint fingerprint)
    {
        var parser = new HtmlParser();
        var document = parser.ParseDocument(html);
        foreach(var removable in document.QuerySelectorAll(RemovableHtmlSelector).ToArray())
            removable.Remove();

        var container = document.QuerySelector(MainElementSelector)
                        ?? document.QuerySelector(ArticleElementSelector)
                        ?? document.QuerySelector("[role=main]")
                        ?? document.Body;
        var extracted = container == null ? string.Empty : ExtractVisibleHtml(container);
        var normalized = NormalizeLineEndings(extracted).Trim();
        DocumentIntakeResult result;
        if (string.IsNullOrWhiteSpace(normalized))
            result = Failure(DocumentIntakeReasonCodes.EmptyContent, EmptyContentDetail);
        else
        {
            var heading = container?.QuerySelector(PrimaryHeadingSelector)?.TextContent.Trim();
            var title = !string.IsNullOrWhiteSpace(heading)
                ? heading
                : !string.IsNullOrWhiteSpace(document.Title)
                    ? document.Title.Trim()
                    : Path.GetFileNameWithoutExtension(fileName);
            result = Success(title,
                             CreateBoundedSections(title, normalized, null, null),
                             fingerprint,
                             rawArtifact: null);
        }

        return result;
    }

    private async Task<DocumentIntakeResult> ReadWithDoclingAsync(DocumentIntakeRequest request,
                                                                  DocumentExtractionFingerprint fingerprint,
                                                                  CancellationToken cancellationToken)
    {
        var file = new DoclingFile(request.FileName, request.MediaType, request.Content);
        var conversion = await mDoclingClient.ConvertAsync(file, cancellationToken);
        DocumentIntakeResult result;
        if (!conversion.Succeeded || conversion.Document == null)
            result = Failure(conversion.ReasonCode, conversion.Detail);
        else
        {
            var document = conversion.Document;
            var title = FindDoclingTitle(document);
            var sections = document.Blocks.Count > 0
                ? CreateDoclingSections(title, document.Blocks)
                : CreateBoundedSections(title,
                                        PreferredDoclingContent(document),
                                        null,
                                        null);
            if (sections.Count == 0)
                result = Failure(DocumentIntakeReasonCodes.EmptyContent, EmptyContentDetail);
            else
            {
                result = Success(title,
                                 sections,
                                 fingerprint,
                                 Encoding.UTF8.GetBytes(document.RawResponseJson));
            }
        }

        return result;
    }

    private IReadOnlyList<DocumentIntakeSection> CreateDoclingSections(string fallbackTitle,
                                                                       IReadOnlyList<DoclingBlock> blocks)
    {
        var result = new List<DocumentIntakeSection>();
        var content = new StringBuilder();
        var title = fallbackTitle;
        int? pageStart = null;
        int? pageEnd = null;
        foreach(var block in blocks.OrderBy(block => block.Order))
        {
            if (block.Kind == DoclingBlockKind.Heading && content.Length > 0)
            {
                AddBoundedSection(result, title, content.ToString(), pageStart, pageEnd);
                content.Clear();
                pageStart = null;
                pageEnd = null;
            }

            if (block.Kind == DoclingBlockKind.Heading && !string.IsNullOrWhiteSpace(block.Text))
                title = block.Text.Trim();
            AppendDoclingBlock(content, block);
            if (block.PageNumber.HasValue)
            {
                pageStart = !pageStart.HasValue
                    ? block.PageNumber
                    : Math.Min(pageStart.Value, block.PageNumber.Value);
                pageEnd = !pageEnd.HasValue
                    ? block.PageNumber
                    : Math.Max(pageEnd.Value, block.PageNumber.Value);
            }
        }

        if (content.Length > 0)
            AddBoundedSection(result, title, content.ToString(), pageStart, pageEnd);
        return result;
    }

    private void AddBoundedSection(List<DocumentIntakeSection> destination,
                                   string title,
                                   string content,
                                   int? pageStart,
                                   int? pageEnd)
    {
        var pieces = SplitBounded(NormalizeLineEndings(content).Trim());
        foreach(var piece in pieces)
            destination.Add(new DocumentIntakeSection(destination.Count, title, piece, pageStart, pageEnd));
    }

    private IReadOnlyList<DocumentIntakeSection> CreateBoundedSections(string title,
                                                                       string content,
                                                                       int? pageStart,
                                                                       int? pageEnd)
    {
        var result = new List<DocumentIntakeSection>();
        AddBoundedSection(result, title, content, pageStart, pageEnd);
        return result;
    }

    private IReadOnlyList<string> SplitBounded(string content)
    {
        var result = new List<string>();
        var offset = 0;
        while (offset < content.Length)
        {
            var count = Math.Min(mLimits.MaxSectionCharacters, content.Length - offset);
            var split = FindSplit(content, offset, count);
            var piece = content.Substring(offset, split).Trim();
            if (!string.IsNullOrWhiteSpace(piece))
                result.Add(piece);
            offset += split;
        }

        return result;
    }

    private int FindSplit(string content, int offset, int count)
    {
        var result = count;
        if (offset + count < content.Length)
        {
            var minimum = count / 2;
            var index = count - 1;
            var found = false;
            while (index >= minimum && !found)
            {
                var value = content[offset + index];
                found = value == '\n' || char.IsWhiteSpace(value);
                if (!found)
                    index--;
            }

            if (found)
                result = index + 1;
        }

        return result;
    }

    private static void AppendDoclingBlock(StringBuilder destination, DoclingBlock block)
    {
        var value = block.Kind switch
            {
                DoclingBlockKind.Table when block.TableCells.Count > 0 => FormatTable(block.TableCells),
                DoclingBlockKind.Heading => FormatHeading(block),
                _ => block.Text.Trim()
            };

        if (!string.IsNullOrWhiteSpace(value))
        {
            if (destination.Length > 0)
                destination.AppendLine().AppendLine();
            destination.Append(value);
        }
    }

    private static string ExtractVisibleHtml(IElement container)
    {
        var result = new StringBuilder();
        var elements = container.QuerySelectorAll(HtmlContentSelector);
        foreach(var element in elements)
            AppendVisibleHtmlElement(result, element);

        if (result.Length == 0)
            result.Append(CollapseWhitespace(container.TextContent));
        return result.ToString();
    }

    private static void AppendVisibleHtmlElement(StringBuilder destination, IElement element)
    {
        var nestedInPre = element.LocalName == CodeElementName
                          && element.ParentElement?.LocalName == PreformattedElementName;
        var text = nestedInPre ? string.Empty : CollapseWhitespace(element.TextContent);
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (destination.Length > 0)
                destination.AppendLine().AppendLine();
            AppendHeadingPrefix(destination, element.LocalName);
            destination.Append(text);
        }
    }

    private static void AppendHeadingPrefix(StringBuilder destination, string localName)
    {
        if (IsHeading(localName))
        {
            var level = int.Parse(localName.AsSpan(start: 1),
                                  System.Globalization.CultureInfo.InvariantCulture);
            destination.Append(new string('#', level)).Append(' ');
        }
    }

    private static bool TryDecode(ReadOnlyMemory<byte> content, out string decoded)
    {
        var bytes = content.ToArray();
        var result = true;
        try
        {
            var format = (StartsWith(bytes, smUtf8Bom),
                          StartsWith(bytes, smUtf16LittleEndianBom),
                          StartsWith(bytes, smUtf16BigEndianBom)) switch
                {
                    (true, _, _) => (Encoding: smStrictUtf8, PreambleLength: smUtf8Bom.Length),
                    (_, true, _) => (Encoding: smStrictUtf16LittleEndian,
                                     PreambleLength: smUtf16LittleEndianBom.Length),
                    (_, _, true) => (Encoding: smStrictUtf16BigEndian,
                                     PreambleLength: smUtf16BigEndianBom.Length),
                    _ => (Encoding: smStrictUtf8, PreambleLength: 0)
                };
            decoded = format.Encoding.GetString(bytes,
                                                format.PreambleLength,
                                                bytes.Length - format.PreambleLength)
                              .TrimStart('\ufeff');
        }
        catch(DecoderFallbackException)
        {
            decoded = string.Empty;
            result = false;
        }

        return result;
    }

    private static bool StartsWith(byte[] value, byte[] prefix)
    {
        var result = value.Length >= prefix.Length;
        var index = 0;
        while (result && index < prefix.Length)
        {
            result = value[index] == prefix[index];
            index++;
        }

        return result;
    }

    private static string FormatTable(IReadOnlyList<DoclingTableCell> cells) =>
        string.Join(TableCellSeparator, cells.OrderBy(cell => cell.StartRow)
                                             .ThenBy(cell => cell.StartColumn)
                                             .Select(cell => cell.Text.Trim()));

    private static string FormatHeading(DoclingBlock block)
    {
        var level = Math.Clamp(block.HeadingLevel ?? 1, 1, MaxMarkdownHeadingLevel);
        return $"{new string('#', level)} {block.Text.Trim()}";
    }

    private static bool HasPdfSignature(ReadOnlyMemory<byte> content) =>
        StartsWith(content.ToArray(), smPdfSignature);

    private static bool HasDocxSignature(ReadOnlyMemory<byte> content)
    {
        var hasContentTypes = false;
        var hasDocument = false;
        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach(var entry in archive.Entries)
            {
                hasContentTypes = hasContentTypes
                                  || entry.FullName.Equals(ContentTypesEntry, StringComparison.OrdinalIgnoreCase);
                hasDocument = hasDocument
                              || entry.FullName.Equals(WordDocumentEntry, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch(InvalidDataException)
        {
            hasContentTypes = false;
            hasDocument = false;
        }

        return hasContentTypes && hasDocument;
    }

    private static string PreferredDoclingContent(DoclingMappedDocument document)
    {
        var result = !string.IsNullOrWhiteSpace(document.MarkdownContent)
            ? document.MarkdownContent
            : document.TextContent;
        return NormalizeLineEndings(result).Trim();
    }

    private static string FindDoclingTitle(DoclingMappedDocument document)
    {
        var heading = document.Blocks.OrderBy(block => block.Order)
                              .FirstOrDefault(block => block.Kind == DoclingBlockKind.Heading
                                                       && !string.IsNullOrWhiteSpace(block.Text));
        var result = heading?.Text.Trim();
        if (string.IsNullOrWhiteSpace(result))
            result = FindMarkdownTitle(PreferredDoclingContent(document), document.FileName);
        return result;
    }

    private static string FindMarkdownTitle(string markdown, string fileName)
    {
        var title = string.Empty;
        var lines = NormalizeLineEndings(markdown).Split('\n');
        var index = 0;
        while (index < lines.Length && string.IsNullOrEmpty(title))
        {
            var line = lines[index].Trim();
            if (line.StartsWith(MarkdownHeadingPrefix, StringComparison.Ordinal))
                title = line[MarkdownHeadingPrefix.Length..].Trim();
            index++;
        }

        var result = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(fileName)
            : title;
        return result;
    }

    private static DocumentIntakeResult Success(string title,
                                                IReadOnlyList<DocumentIntakeSection> sections,
                                                DocumentExtractionFingerprint fingerprint,
                                                byte[]? rawArtifact)
    {
        var artifact = rawArtifact
                       ?? JsonSerializer.SerializeToUtf8Bytes(new
                                                                  {
                                                                      title,
                                                                      sections
                                                                  });
        return new DocumentIntakeResult(true,
                                        DocumentIntakeReasonCodes.Extracted,
                                        ExtractedDetail,
                                        title,
                                        sections,
                                        artifact,
                                        JsonMediaType,
                                        new DocumentExtractionProvenance
                                            {
                                                ExtractorName = fingerprint.ExtractorName,
                                                ExtractorVersion = fingerprint.ExtractorVersion,
                                                ConfigurationHash = fingerprint.ConfigurationHash,
                                                UsedOcr = fingerprint.UsedOcr
                                            });
    }

    DocumentExtractionFingerprint IDocumentIntakeFingerprintProvider.GetFingerprint(string fileName,
                                                                                     string mediaType)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentException.ThrowIfNullOrEmpty(mediaType);
        return CreateFingerprint(fileName, mediaType);
    }

    private DocumentExtractionFingerprint CreateFingerprint(string fileName, string mediaType)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        (string ExtractorName, string Contract, bool UsedOcr, bool CanReuse) settings = extension switch
            {
                ".md" or ".markdown" or ".txt" or ".text" =>
                    (LocalExtractorName, LocalExtractionContract, false, true),
                ".html" or ".htm" => (HtmlExtractorName, HtmlExtractionContract, false, true),
                ".pdf" or ".docx" => (DoclingExtractorName, DoclingExtractionContract, true, false),
                _ => (UnsupportedExtractorName, UnsupportedExtractionContract, false, false)
            };
        string sectionLimit = mLimits.MaxSectionCharacters.ToString(CultureInfo.InvariantCulture);
        string configuration = string.Join(ConfigurationSeparator,
                                           ConfigurationSchema,
                                           settings.ExtractorName,
                                           ExtractorVersion,
                                           mediaType.Trim().ToLowerInvariant(),
                                           sectionLimit,
                                           settings.Contract);
        string configurationHash = Convert.ToHexString(
                                               SHA256.HashData(Encoding.UTF8.GetBytes(configuration)))
                                          .ToLowerInvariant();
        return new DocumentExtractionFingerprint(settings.ExtractorName,
                                                 ExtractorVersion,
                                                 configurationHash,
                                                 settings.UsedOcr,
                                                 settings.CanReuse);
    }

    private static DocumentIntakeResult Failure(string reasonCode, string detail) =>
        new(false,
            reasonCode,
            detail,
            string.Empty,
            [],
            ReadOnlyMemory<byte>.Empty,
            string.Empty,
            null);

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
             .Replace('\r', '\n');

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsHeading(string localName) =>
        localName.Length == 2 && localName[0] == 'h' && localName[1] is >= '1' and <= '6';

    private static readonly Encoding smStrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
                                                                      throwOnInvalidBytes: true);
    private static readonly Encoding smStrictUtf16LittleEndian = new UnicodeEncoding(bigEndian: false,
                                                                                      byteOrderMark: true,
                                                                                      throwOnInvalidBytes: true);
    private static readonly Encoding smStrictUtf16BigEndian = new UnicodeEncoding(bigEndian: true,
                                                                                   byteOrderMark: true,
                                                                                   throwOnInvalidBytes: true);
    private static readonly byte[] smUtf8Bom = [0xef, 0xbb, 0xbf];
    private static readonly byte[] smUtf16LittleEndianBom = [0xff, 0xfe];
    private static readonly byte[] smUtf16BigEndianBom = [0xfe, 0xff];
    private static readonly byte[] smPdfSignature = "%PDF-"u8.ToArray();

    private const string ContentTypesEntry = "[Content_Types].xml";
    private const string WordDocumentEntry = "word/document.xml";
    private const string MainElementSelector = "main";
    private const string ArticleElementSelector = "article";
    private const string PrimaryHeadingSelector = "h1";
    private const string CodeElementName = "code";
    private const string PreformattedElementName = "pre";
    private const string TableCellSeparator = " | ";
    private const string MarkdownHeadingPrefix = "# ";
    private const string RemovableHtmlSelector = "script, style, noscript, nav, footer, aside";
    private const string HtmlContentSelector = "h1, h2, h3, h4, h5, h6, p, li, pre, code, table";
    private const string JsonMediaType = "application/json";
    private const string LocalExtractorName = "SaddleRAG.LocalText";
    private const string HtmlExtractorName = "SaddleRAG.AngleSharp";
    private const string DoclingExtractorName = "Docling";
    private const string UnsupportedExtractorName = "SaddleRAG.Unsupported";
    private const string ExtractorVersion = "1";
    private const string ConfigurationSchema = "saddlerag-document-extraction-v1";
    private const string LocalExtractionContract = "strict-text;lf;markdown-title;json-sections-v1";
    private const string HtmlExtractionContract = "anglesharp;visible-main-content;json-sections-v1";
    private const string DoclingExtractionContract =
        "serve-v1;markdown+json+text;ocr=true;table=accurate;pipeline=standard;images=placeholder;enrichment=false;abort=false;mapper=v1";
    private const string UnsupportedExtractionContract = "unsupported";
    private const char ConfigurationSeparator = '|';
    private const string ExtractedDetail = "The document was extracted successfully.";
    private const string UnsupportedFormatDetail = "The document format is not supported.";
    private const string UnsupportedEncodingDetail = "The document text encoding is not supported.";
    private const string EmptyContentDetail = "The document contains no searchable text.";
    private const string InvalidSignatureDetail = "The document bytes do not match the expected file format.";
    private const int MaxMarkdownHeadingLevel = 6;
}
