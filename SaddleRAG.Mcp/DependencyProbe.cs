// DependencyProbe.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Mcp;

/// <summary>A lightweight check that never loads a model or converts a document.</summary>
internal sealed record DependencyProbe(string Name, bool Required, string AffectedWork, string Remediation,
                                      Func<CancellationToken, Task<string>> Check);
