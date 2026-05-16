// MonitorConfigExecutionProvider.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     ONNX execution-provider card on the Monitor /config page (issue #73).
///     <see cref="Requested" /> is what configuration asked for (CPU /
///     DirectMl / Cuda / CoreML); <see cref="Active" /> is what the runtime
///     actually loaded — they can diverge when a requested GPU EP isn't
///     present on the box and the configurator falls back to CPU.
/// </summary>
public sealed record MonitorConfigExecutionProvider(
    string Requested,
    string Active,
    bool MatchesRequested,
    IReadOnlyList<string> CompiledInProviders,
    string? LastLoadWarning);
