// DoclingInstallationGuide.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>Structured installation documentation shown by SaddleRAG without executing it.</summary>
public sealed record DoclingInstallationGuide(string CompatibilityTestedVersion,
                                              string OfficialInstallUrl,
                                              string OfficialReleaseUrl,
                                              string OfficialApiUrl,
                                              string Endpoint,
                                              string HealthTestUrl,
                                              string Instructions,
                                              string OwnershipNotice);
