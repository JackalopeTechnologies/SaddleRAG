// ComponentStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>The result of checking one component.</summary>
/// <param name="Kind">Which component was checked.</param>
/// <param name="State">What the check found.</param>
/// <param name="Detail">A short reason the operator can read, such as why a running service is unreachable.</param>
public sealed record ComponentStatus(ComponentKind Kind, ComponentState State, string Detail);