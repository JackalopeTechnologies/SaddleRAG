// ListeningProcess.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>The process that is listening on a local TCP port.</summary>
/// <param name="ProcessId">The Windows process id.</param>
/// <param name="ProcessName">The process name without the extension, such as <c>ollama</c>.</param>
/// <param name="ExecutablePath">
///     The program file, or null when Windows will not tell this user (another user's or an elevated process).
/// </param>
public sealed record ListeningProcess(int ProcessId, string ProcessName, string? ExecutablePath);