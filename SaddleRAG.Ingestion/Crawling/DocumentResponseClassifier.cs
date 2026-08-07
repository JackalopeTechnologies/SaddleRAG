// DocumentResponseClassifier.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Classifies exact response bytes using authoritative response metadata
///     and file signatures. A URL suffix is only a hint for generic binary
///     media types and can never classify arbitrary bytes as a document.
/// </summary>
public static class DocumentResponseClassifier
{
    public static DocumentResponseKind Classify(FetchedWebResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        string mediaType = ReadMediaType(response.Headers);
        ReadOnlySpan<byte> body = response.Body.Span;
        bool pdfSignature = HasPdfSignature(body);
        bool docxSignature = HasDocxSignature(body);
        DocumentResponseKind result;

        switch(true)
        {
            case true when IsHtmlMediaType(mediaType):
                result = DocumentResponseKind.Html;
                break;
            case true when mediaType.Equals(PdfMediaType, StringComparison.OrdinalIgnoreCase):
                result = pdfSignature ? DocumentResponseKind.Pdf : DocumentResponseKind.Other;
                break;
            case true when mediaType.Equals(DocxMediaType, StringComparison.OrdinalIgnoreCase):
                result = docxSignature ? DocumentResponseKind.Docx : DocumentResponseKind.Other;
                break;
            case true when IsGenericBinaryMediaType(mediaType) && HasSuffixHint(response, PdfExtension):
                result = pdfSignature ? DocumentResponseKind.Pdf : DocumentResponseKind.Other;
                break;
            case true when IsGenericBinaryMediaType(mediaType) && HasSuffixHint(response, DocxExtension):
                result = docxSignature ? DocumentResponseKind.Docx : DocumentResponseKind.Other;
                break;
            default:
                result = DocumentResponseKind.Other;
                break;
        }

        return result;
    }

    private static string ReadMediaType(IReadOnlyDictionary<string, string> headers)
    {
        string result = string.Empty;
        if (headers.TryGetValue(ContentTypeHeader, out string? value))
        {
            int parameterSeparator = value.IndexOf(';', StringComparison.Ordinal);
            result = (parameterSeparator >= 0 ? value[..parameterSeparator] : value).Trim();
        }

        return result;
    }

    private static bool IsHtmlMediaType(string mediaType) =>
        mediaType.Equals(HtmlMediaType, StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals(XhtmlMediaType, StringComparison.OrdinalIgnoreCase);

    private static bool IsGenericBinaryMediaType(string mediaType) =>
        string.IsNullOrEmpty(mediaType)
        || mediaType.Equals(OctetStreamMediaType, StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals(BinaryOctetStreamMediaType, StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals(ZipMediaType, StringComparison.OrdinalIgnoreCase);

    private static bool HasPdfSignature(ReadOnlySpan<byte> body) =>
        body.Length >= PdfSignature.Length && body[..PdfSignature.Length].SequenceEqual(PdfSignature);

    private static bool HasDocxSignature(ReadOnlySpan<byte> body) =>
        body.Length >= ZipSignature.Length && body[..ZipSignature.Length].SequenceEqual(ZipSignature);

    private static bool HasSuffixHint(FetchedWebResponse response, string extension) =>
        HasSuffix(response.FinalUrl, extension)
        || HasSuffix(response.AttemptedUrl, extension)
        || HasSuffix(response.OriginalUrl, extension);

    private static bool HasSuffix(string url, string extension)
    {
        var result = false;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            result = uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private const string BinaryOctetStreamMediaType = "binary/octet-stream";
    private const string ContentTypeHeader = "content-type";
    private const string DocxExtension = ".docx";
    private const string DocxMediaType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string HtmlMediaType = "text/html";
    private const string OctetStreamMediaType = "application/octet-stream";
    private const string PdfExtension = ".pdf";
    private const string PdfMediaType = "application/pdf";
    private const string XhtmlMediaType = "application/xhtml+xml";
    private const string ZipMediaType = "application/zip";

    private static ReadOnlySpan<byte> PdfSignature => "%PDF-"u8;
    private static ReadOnlySpan<byte> ZipSignature => [0x50, 0x4B, 0x03, 0x04];
}
