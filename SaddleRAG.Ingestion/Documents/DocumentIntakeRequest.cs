// DocumentIntakeRequest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Intake;

/// <summary>Immutable file passed from stable acquisition to format intake.</summary>
public sealed record DocumentIntakeRequest(string FileName,
                                           string RelativePath,
                                           string MediaType,
                                           ReadOnlyMemory<byte> Content);
