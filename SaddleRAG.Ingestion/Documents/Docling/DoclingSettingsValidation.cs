// DoclingSettingsValidation.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>Result of local Docling configuration validation.</summary>
public sealed record DoclingSettingsValidation(bool IsValid,
                                               string ReasonCode,
                                               string Detail,
                                               Uri? Endpoint);
