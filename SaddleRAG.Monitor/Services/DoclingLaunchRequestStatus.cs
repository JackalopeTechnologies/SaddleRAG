// DoclingLaunchRequestStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Whether document work is waiting on a Docling that is not ready. This reports
///     state; it does not command anything. The tray reads it and decides for itself.
/// </summary>
public sealed record DoclingLaunchRequestStatus(bool LaunchRequested, string ReasonCode);
