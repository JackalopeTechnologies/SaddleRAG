// SupportedDocumentIngestionException.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Fatal extraction failure for a response positively identified as a
///     supported document. The stable reason and observed detail are retained
///     for the caller and LLM-facing diagnostics.
/// </summary>
public sealed class SupportedDocumentIngestionException : Exception
{
    public SupportedDocumentIngestionException(string reasonCode, string detail)
        : base(BuildMessage(reasonCode, detail))
    {
        ReasonCode = reasonCode;
        Detail = detail;
    }

    public string ReasonCode { get; }

    public string Detail { get; }

    private static string BuildMessage(string reasonCode, string detail)
    {
        ArgumentException.ThrowIfNullOrEmpty(reasonCode);
        ArgumentException.ThrowIfNullOrEmpty(detail);
        return $"Supported document ingestion failed ({reasonCode}): {detail}";
    }
}
