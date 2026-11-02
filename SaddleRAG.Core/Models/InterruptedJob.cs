// InterruptedJob.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>A stopped job awaiting persistence of its final failure status.</summary>
public sealed record InterruptedJob(string JobId, string? Profile, string Detail);
