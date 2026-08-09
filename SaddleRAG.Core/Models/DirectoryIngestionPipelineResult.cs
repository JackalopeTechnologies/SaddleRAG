// DirectoryIngestionPipelineResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Complete processing manifest returned before a directory version may publish.</summary>
public sealed record DirectoryIngestionPipelineResult(int DocumentsProcessed,
                                                      int PagesIndexed,
                                                      int ChunksIndexed,
                                                      string EmbeddingProviderId,
                                                      string EmbeddingModelName,
                                                      int EmbeddingDimensions,
                                                      string? ClassifierBackend,
                                                      string? ClassifierModel,
                                                      string? SubjectTaxonomyVersion);
