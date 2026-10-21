// LogNotificationStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Mcp;

/// <summary>Unread warnings and errors from the bounded server log window.</summary>
public sealed record LogNotificationStatus(int Warnings, int Errors, DateTimeOffset? NewestAt, bool ReadFailed)
{
    public bool HasWarnings { get; init; }
    public bool HasErrors { get; init; }
}
