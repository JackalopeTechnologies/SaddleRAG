// IDoclingClient.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>
///     Read-only HTTP boundary to a user-managed Docling Serve process.
/// </summary>
public interface IDoclingClient
{
    Task<DoclingServiceObservation> CheckHealthAsync(CancellationToken cancellationToken = default);

    Task<DoclingServiceObservation> CheckReadinessAsync(CancellationToken cancellationToken = default);

    Task<DoclingConversionResult> ConvertAsync(DoclingFile file,
                                               CancellationToken cancellationToken = default);
}
