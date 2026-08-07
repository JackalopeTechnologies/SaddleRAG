// DirectoryVersionClaimResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;

namespace SaddleRAG.Core.Models;

/// <summary>Result of atomically claiming one manual directory version.</summary>
public sealed record DirectoryVersionClaimResult(DirectoryVersionClaimStatus Status,
                                                 bool RequiresCleanup = false);
