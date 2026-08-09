// DirectoryScanLimits.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Named safety bounds for directory previews.</summary>
public static class DirectoryScanLimits
{
    public const int DefaultMaxDocumentCount = 10000;
    public const int DefaultMaxDirectoryCount = 10000;
    public const int DefaultMaxEntryCount = 100000;
    public const long DefaultMaxFileBytes = 64L * 1024L * 1024L;
    public const int DefaultMaxSectionCount = 100000;
    public const long DefaultMaxSpoolBytes = 1024L * 1024L * 1024L;
    public const long DefaultMaxTotalBytes = 1024L * 1024L * 1024L;
    public const int DefaultMaxChunkCount = 100000;
    public const string DocxExtension = ".docx";
    public const string HtmExtension = ".htm";
    public const string HtmlExtension = ".html";
    public const string MarkdownLongExtension = ".markdown";
    public const string MarkdownExtension = ".md";
    public const string PdfExtension = ".pdf";
    public const string TextLongExtension = ".text";
    public const string TextExtension = ".txt";

    public static IReadOnlyList<string> SupportedExtensions { get; } =
        [
            DocxExtension,
            HtmExtension,
            HtmlExtension,
            MarkdownLongExtension,
            MarkdownExtension,
            PdfExtension,
            TextLongExtension,
            TextExtension
        ];
}
