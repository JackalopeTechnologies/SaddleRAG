// McpToolExceptionFilter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

#endregion

namespace SaddleRAG.Mcp;

/// <summary>
///     Global <c>tools/call</c> filter that converts argument-validation
///     exceptions thrown inside MCP tool methods into structured
///     <see cref="CallToolResult" /> responses with <c>IsError = true</c>,
///     surfacing the validation message to the calling LLM so it can retry
///     with corrected parameters instead of seeing a generic
///     "An error occurred invoking 'X'" reply.
///     <para>
///         Only <see cref="ArgumentException" /> (which covers
///         <see cref="ArgumentNullException" /> and
///         <see cref="ArgumentOutOfRangeException" />) is intercepted —
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
            result = await next();
        }
        catch(ArgumentException ex)
        {
            LogArgumentError(services, toolName, ex);
            result = BuildArgumentErrorResult(toolName, ex);
        }
        return result;
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
