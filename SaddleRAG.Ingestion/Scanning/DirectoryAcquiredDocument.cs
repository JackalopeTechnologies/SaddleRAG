// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Ingestion.Documents.Intake;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>A stable local file together with its normalized extraction.</summary>
public sealed record DirectoryAcquiredDocument(DirectoryStableDocument Source,
                                               DocumentIntakeResult Intake);
