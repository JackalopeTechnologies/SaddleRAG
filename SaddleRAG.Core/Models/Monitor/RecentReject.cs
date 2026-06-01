// RecentReject.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models.Monitor;

public sealed record RecentReject
{
    public required string Url { get; init; }
    public required string Reason { get; init; }
    public DateTime At { get; init; }
}
