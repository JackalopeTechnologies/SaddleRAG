// MonitorConfigExecutionProvider.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     ONNX execution-provider card on the Monitor /config page (issue #73).
///     <see cref="Requested" /> is what configuration asked for (CPU /
///     DirectMl / Cuda / CoreML); <see cref="Active" /> is what the runtime
///     actually loaded — they can diverge when a requested GPU EP isn't
///     present on the box and the configurator falls back to CPU.
///     The device-loss fields (issue #144) surface the self-healing state:
///     <see cref="DeviceLossRecoveryCount" /> incidents recovered since
///     process start, and <see cref="DeviceLossFallbackActive" /> when
///     repeated GPU loss forced the sessions onto the CPU provider.
/// </summary>
public sealed record MonitorConfigExecutionProvider(
    string Requested,
    string Active,
    bool MatchesRequested,
    IReadOnlyList<string> CompiledInProviders,
    string? LastLoadWarning,
    int DeviceLossRecoveryCount = 0,
    bool DeviceLossFallbackActive = false,
    DateTime? LastDeviceLossUtc = null);
