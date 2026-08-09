// IDocumentIntake.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Ingestion.Documents.Intake;

/// <summary>Routes immutable document bytes to local extraction or Docling.</summary>
public interface IDocumentIntake
{
    Task<DocumentIntakeResult> ReadAsync(DocumentIntakeRequest request,
                                         CancellationToken cancellationToken = default);
}
