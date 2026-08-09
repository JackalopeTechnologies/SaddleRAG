// DocumentIntakeSection.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Intake;

/// <summary>One bounded normalized section produced by document intake.</summary>
public sealed record DocumentIntakeSection(int Order,
                                           string Title,
                                           string Content,
                                           int? PageStart,
                                           int? PageEnd);
