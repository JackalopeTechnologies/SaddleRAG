// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Intake;

/// <summary>Identifies the SaddleRAG extraction contract used for one document.</summary>
internal sealed record DocumentExtractionFingerprint(string ExtractorName,
                                                     string ExtractorVersion,
                                                     string ConfigurationHash,
                                                     bool UsedOcr,
                                                     bool CanReuseBeforeExtraction);
