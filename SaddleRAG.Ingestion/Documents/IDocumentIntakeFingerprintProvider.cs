// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Intake;

/// <summary>Supplies the extraction contract before document conversion begins.</summary>
internal interface IDocumentIntakeFingerprintProvider
{
    DocumentExtractionFingerprint GetFingerprint(string fileName, string mediaType);
}
