// McpToolExceptionFilter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Mcp;

/// <summary>
///     Global <c>tools/call</c> filter that converts the failures a calling LLM
///     can act on into structured <see cref="CallToolResult" /> responses with
///     <c>IsError = true</c>, instead of the generic
///     "An error occurred invoking 'X'" reply.
///     <para>
///         Two kinds are intercepted. <see cref="ArgumentException" /> (which covers
///         <see cref="ArgumentNullException" /> and
///         <see cref="ArgumentOutOfRangeException" />) surfaces the validation message
///         so the caller can retry with corrected parameters. A database outage, as
///         judged by <see cref="DatabaseFailure.AsUnavailable" />, surfaces which
///         database endpoint is unreachable and how to fix it, so the caller can tell
///         the user instead of guessing the tool is broken.
///     </para>
///     <para>
///         Each tool runs inside a <see cref="DatabaseFailFastScope" />, so its database
///         calls stop waiting once an outage is established.
///     </para>
///     <para>
///         <see cref="OperationCanceledException" />, <see cref="McpException" />,
///         and unrelated exceptions are left to propagate so the framework
///         handles them as designed.
///     </para>
/// </summary>
internal static class McpToolExceptionFilter
{
    /// <summary>
    ///     Wires the call-tool filter into the MCP server pipeline. Apply
    ///     once during startup after <c>AddMcpServer().WithHttpTransport()</c>
    ///     and before <c>WithToolsFromAssembly()</c>.
    /// </summary>
    public static IMcpServerBuilder UseToolExceptionFilter(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithRequestFilters(filters => filters.AddCallToolFilter(Wrap));
        return builder;
    }

    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return (context, cancellationToken) => ExecuteAsync(context.Params?.Name,
                                                            context.Services,
                                                            () => next(context, cancellationToken)
                                                           );
    }

    internal static async ValueTask<CallToolResult> ExecuteAsync(string? toolName,
                                                                 IServiceProvider? services,
                                                                 Func<ValueTask<CallToolResult>> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        CallToolResult result;
        try
        {
            // Only database calls made by a tool call may fail fast during an outage.
            using(DatabaseFailFastScope.Begin())
                result = await next();
        }
        catch(ArgumentException ex)
        {
            LogArgumentError(services, toolName, ex);
            result = BuildArgumentErrorResult(toolName, ex);
        }
        catch(Exception ex) when (DatabaseFailure.AsUnavailable(ex, GetUnreachableEndpoints(services)) is { } unavailable)
        {
            LogDatabaseUnavailable(services, toolName, unavailable);
            result = BuildDatabaseUnavailableResult(unavailable);
        }
        return result;
    }

    internal static CallToolResult BuildDatabaseUnavailableResult(DatabaseUnavailableException unavailable)
    {
        ArgumentNullException.ThrowIfNull(unavailable);

        return new CallToolResult
                   {
                       Content = [new TextContentBlock { Text = unavailable.Message }],
                       IsError = true
                   };
    }

    private static IReadOnlyList<string> GetUnreachableEndpoints(IServiceProvider? services) =>
        services?.GetService<SaddleRagDbContextFactory>()?.GetUnreachableEndpoints() ?? [];

    // Error, not Warning: the tool call itself fails and nothing retries it. Before
    // this filter mapped the outage, the SDK logged the same failure at Error.
    private static void LogDatabaseUnavailable(IServiceProvider? services,
                                               string? toolName,
                                               DatabaseUnavailableException unavailable)
    {
        ILogger? logger = services?.GetService<ILoggerFactory>()
                                  ?.CreateLogger(LoggerCategory);
        logger?.LogError(unavailable,
                         "MCP tool '{Tool}' failed: the database at {Endpoint} is unreachable",
                         toolName ?? UnknownToolName,
                         unavailable.Endpoint
                        );
    }

    internal static CallToolResult BuildArgumentErrorResult(string? toolName, ArgumentException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string name = string.IsNullOrEmpty(toolName) ? UnknownToolName : toolName;
        string message = $"Invalid argument for '{name}': {exception.Message}";

        return new CallToolResult
                   {
                       Content = [new TextContentBlock { Text = message }],
                       IsError = true
                   };
    }

    private static void LogArgumentError(IServiceProvider? services, string? toolName, ArgumentException exception)
    {
        ILogger? logger = services?.GetService<ILoggerFactory>()
                                  ?.CreateLogger(LoggerCategory);
        logger?.LogWarning(exception,
                           "MCP tool '{Tool}' rejected with {ExceptionType}: {Message}",
                           toolName ?? UnknownToolName,
                           exception.GetType().Name,
                           exception.Message
                          );
    }

    private const string UnknownToolName = "<unknown>";
    private const string LoggerCategory = "SaddleRAG.Mcp.McpToolExceptionFilter";
}
