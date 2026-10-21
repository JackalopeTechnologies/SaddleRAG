// TrayAttention.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>Persistent tray presentation and the identity of a new operator notification.</summary>
public sealed record TrayAttention(McpServiceState IconState, string Summary, string TargetPath,
                                    string NotificationKey)
{
    private const string UnreachableSummary = "SaddleRAG health is unavailable — open health";
    private const string HealthPage = "/monitor/health";
    private const string UnreachableKey = "health-unreachable";
    public static TrayAttention Unreachable { get; } =
        new(McpServiceState.Stopped, UnreachableSummary, HealthPage, UnreachableKey);
}
