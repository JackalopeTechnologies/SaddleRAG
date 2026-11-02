// ComponentActionResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>The result of one operator action on one component.</summary>
/// <param name="Kind">Which component the action was on.</param>
/// <param name="Status">How it ended.</param>
/// <param name="Message">A sentence the operator can read, saying what happened and, on failure, what to do.</param>
public sealed record ComponentActionResult(ComponentKind Kind, ComponentActionStatus Status, string Message);
