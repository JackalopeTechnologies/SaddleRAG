// DoclingConversionResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>Result of a Docling conversion request.</summary>
public sealed record DoclingConversionResult(bool Succeeded,
                                             string ReasonCode,
                                             string Detail,
                                             DoclingMappedDocument? Document,
                                             string RawResponseJson)
{
    public static DoclingConversionResult Success(DoclingMappedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new DoclingConversionResult(true,
                                           DoclingReasonCodes.Ready,
                                           SuccessDetail,
                                           document,
                                           document.RawResponseJson);
    }

    public static DoclingConversionResult Failure(string reasonCode,
                                                  string detail,
                                                  string rawResponseJson = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(reasonCode);
        ArgumentException.ThrowIfNullOrEmpty(detail);
        return new DoclingConversionResult(false, reasonCode, detail, null, rawResponseJson);
    }

    private const string SuccessDetail = "Docling converted the document successfully.";
}
