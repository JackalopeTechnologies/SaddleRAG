// DocumentIntakeReasonCodes.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Intake;

/// <summary>Stable outcomes from format routing and content extraction.</summary>
public static class DocumentIntakeReasonCodes
{
    public const string Extracted = "DOCUMENT_EXTRACTED";
    public const string UnsupportedFormat = "DOCUMENT_FORMAT_UNSUPPORTED";
    public const string InvalidSignature = "DOCUMENT_SIGNATURE_INVALID";
    public const string UnsupportedEncoding = "DOCUMENT_ENCODING_UNSUPPORTED";
    public const string EmptyContent = "DOCUMENT_CONTENT_EMPTY";
    public const string ExtractionFailed = "DOCUMENT_EXTRACTION_FAILED";
}
