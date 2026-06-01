// MonitorConfigService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Surfaces the SaddleRAG MCP runtime configuration to the Monitor
///     /config page (issue #73). The concrete implementation lives in the
///     SaddleRAG.Mcp host project so this Monitor assembly can keep its
///     Core-only project-reference footprint; the host wires its own
///     <c>OnnxSettings</c> / <c>OllamaSettings</c> / <c>SaddleRagDbSettings</c>
///     / <c>OnnxRuntimeCapabilities</c> into the snapshot.
/// </summary>
public interface IMonitorConfigSource
{
    /// <summary>
    ///     Build a fresh snapshot of the current runtime configuration.
    ///     Implementations must be cheap (no I/O) and safe to call from
    ///     the Razor render thread.
    /// </summary>
    MonitorConfigSnapshot GetSnapshot();
}
