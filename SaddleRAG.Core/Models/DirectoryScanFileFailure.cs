// DirectoryScanFileFailure.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     One sanitized file-level failure from a manual directory scan.
/// </summary>
public sealed record DirectoryScanFileFailure(string RelativePath,
                                              string ReasonCode,
                                              string Detail);
