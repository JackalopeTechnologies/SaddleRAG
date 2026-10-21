// ShutdownConnectionMiddleware.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Mcp;

/// <summary>Closes long-lived client connections when the host begins shutting down.</summary>
public sealed class ShutdownConnectionMiddleware
{
    private const string McpPath = "/mcp";
    private const string BlazorPath = "/_blazor";
    private const string MonitorHubPath = "/monitor/hub";
    private readonly RequestDelegate mNext;
    private readonly IHostApplicationLifetime mLifetime;

    public ShutdownConnectionMiddleware(RequestDelegate next, IHostApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(lifetime);
        mNext = next;
        mLifetime = lifetime;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Open event streams and browser circuits otherwise consume the entire host
        // shutdown deadline. WindowsServiceLifetime then receives an expired token
        // and turns an ordinary Stop into a crash and a locked crash-dump process.
        bool longLived = context.Request.Path.StartsWithSegments(McpPath)
                         || context.Request.Path.StartsWithSegments(BlazorPath)
                         || context.Request.Path.StartsWithSegments(MonitorHubPath);
        using CancellationTokenRegistration registration = longLived
            ? mLifetime.ApplicationStopping.Register(context.Abort)
            : default;
        await mNext(context);
    }
}
