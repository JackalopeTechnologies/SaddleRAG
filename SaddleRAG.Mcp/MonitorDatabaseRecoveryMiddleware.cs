// MonitorDatabaseRecoveryMiddleware.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Mcp;

/// <summary>
///     Before a Monitor request (<c>/api/monitor/...</c>) runs, makes sure the database is
///     answering, restarting a stopped local MongoDB service if that is what is wrong. If the
///     database still cannot be reached, the request gets a short HTTP 503 reply with a plain
///     JSON message instead of an error page, so callers such as the tray can handle it quietly.
///     Any other failure is left alone.
/// </summary>
public sealed class MonitorDatabaseRecoveryMiddleware
{
    public MonitorDatabaseRecoveryMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        mNext = next;
    }

    private readonly RequestDelegate mNext;

    public async Task InvokeAsync(HttpContext context,
                                  DatabaseRecovery recovery,
                                  SaddleRagDbContextFactory factory,
                                  ILogger<MonitorDatabaseRecoveryMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);

        if (context.Request.Path.StartsWithSegments(MonitorPath))
            await InvokeMonitorAsync(context, recovery, factory, logger);
        else
            await mNext(context);
    }

    private async Task InvokeMonitorAsync(HttpContext context,
                                          DatabaseRecovery recovery,
                                          SaddleRagDbContextFactory factory,
                                          ILogger<MonitorDatabaseRecoveryMiddleware> logger)
    {
        try
        {
            await recovery.EnsureAvailableAsync(context.RequestAborted);
            await mNext(context);
        }
        catch(Exception ex)
        {
            // A reply that has already started cannot be replaced with a 503.
            DatabaseUnavailableException? unavailable = context.Response.HasStarted
                                                            ? null
                                                            : DatabaseFailure.AsUnavailable(ex,
                                                                    factory.GetUnreachableEndpoints()
                                                                );
            if (unavailable == null)
                throw;

            logger.LogWarning("Monitor request {Path} rejected: the database at {Endpoint} is unavailable.",
                              context.Request.Path.Value,
                              unavailable.Endpoint
                             );
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new OutageReply(unavailable.Message), context.RequestAborted);
        }
    }

    private sealed record OutageReply(string Error);

    private const string MonitorPath = "/api/monitor";
}
