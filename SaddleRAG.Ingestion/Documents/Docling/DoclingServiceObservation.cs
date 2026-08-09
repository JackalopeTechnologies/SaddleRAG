// DoclingServiceObservation.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>Observed result from one Docling liveness or readiness request.</summary>
public sealed record DoclingServiceObservation(bool Succeeded,
                                               string ReasonCode,
                                               string Detail)
{
    public static DoclingServiceObservation Success(string detail = DefaultSuccessDetail)
    {
        ArgumentException.ThrowIfNullOrEmpty(detail);
        return new DoclingServiceObservation(true, DoclingReasonCodes.Ready, detail);
    }

    public static DoclingServiceObservation Failure(string reasonCode, string detail)
    {
        ArgumentException.ThrowIfNullOrEmpty(reasonCode);
        ArgumentException.ThrowIfNullOrEmpty(detail);
        return new DoclingServiceObservation(false, reasonCode, detail);
    }

    private const string DefaultSuccessDetail = "Docling responded successfully.";
}
