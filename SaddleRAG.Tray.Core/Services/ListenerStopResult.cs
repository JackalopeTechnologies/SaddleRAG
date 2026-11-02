// ListenerStopResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>How stopping Docling or Ollama ended, and whether its port was then seen free.</summary>
/// <param name="Result">The result to report to the operator.</param>
/// <param name="PortFreed">
///     True only when the tray stopped the process and then saw nothing listening on the port. A
///     restart may let Docling launch again only in that case.
/// </param>
internal sealed record ListenerStopResult(ComponentActionResult Result, bool PortFreed);
