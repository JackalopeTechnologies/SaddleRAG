// DirectoryAccessTestResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Monitor.Services;

/// <summary>Read-only validation result for a user-selected directory root.</summary>
public sealed record DirectoryAccessTestResult(bool Succeeded,
                                               string ReasonCode,
                                               string Detail);
