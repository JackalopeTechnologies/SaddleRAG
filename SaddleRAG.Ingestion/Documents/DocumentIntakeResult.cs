// DocumentIntakeResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Ingestion.Documents.Intake;

/// <summary>Normalized extraction result independent of filesystem location.</summary>
public sealed record DocumentIntakeResult(bool Succeeded,
                                          string ReasonCode,
                                          string Detail,
                                          string Title,
                                          IReadOnlyList<DocumentIntakeSection> Sections,
                                          ReadOnlyMemory<byte> ExtractionArtifact,
                                          string ExtractionMediaType,
                                          DocumentExtractionProvenance? Provenance);
