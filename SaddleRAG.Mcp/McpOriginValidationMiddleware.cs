// McpOriginValidationMiddleware.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Mcp;

/// <summary>Rejects browser requests from untrusted origins before the MCP transport runs.</summary>
internal sealed class McpOriginValidationMiddleware
{
    private const string AllowedOriginsKey = "Mcp:AllowedOrigins";
    private const string InvalidOriginConfiguration = "Mcp:AllowedOrigins must contain HTTP or HTTPS origins without paths or credentials.";
    private const string McpPath = "/mcp";
    private const string OriginHeader = "Origin";
    private const string HttpScheme = "http";
    private const string HttpsScheme = "https";
    private const string RootPath = "/";
    private readonly RequestDelegate mNext;
    private readonly HashSet<string> mAllowedOrigins;

    public McpOriginValidationMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(configuration);
        mNext = next;
        mAllowedOrigins = new HashSet<string>(StringComparer.Ordinal);
        foreach (string origin in configuration.GetSection(AllowedOriginsKey).Get<string[]>() ?? [])
        {
            if (!TryParseOrigin(origin, out Uri? parsed) || parsed is null)
                throw new InvalidOperationException(InvalidOriginConfiguration);

            mAllowedOrigins.Add(parsed.GetLeftPart(UriPartial.Authority));
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool allowed = true;
        if (context.Request.Path.StartsWithSegments(McpPath) && context.Request.Headers.TryGetValue(OriginHeader, out var origins))
        {
            allowed = origins.Count == 1 && TryParseOrigin(origins[0], out Uri? origin) && origin is not null &&
                      (mAllowedOrigins.Contains(origin.GetLeftPart(UriPartial.Authority)) || IsLocalSameOrigin(origin, context.Request));
        }

        if (allowed)
            await mNext(context);
        else
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
    }

    private static bool IsLocalSameOrigin(Uri origin, HttpRequest request)
    {
        bool same = origin.IsLoopback && Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out Uri? target) &&
                    string.Equals(origin.GetLeftPart(UriPartial.Authority), target.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);
        return same;
    }

    private static bool TryParseOrigin(string? value, out Uri? origin)
    {
        bool valid = Uri.TryCreate(value, UriKind.Absolute, out origin) &&
                     origin.Scheme is HttpScheme or HttpsScheme && string.IsNullOrEmpty(origin.UserInfo) &&
                     origin.AbsolutePath == RootPath && string.IsNullOrEmpty(origin.Query) && string.IsNullOrEmpty(origin.Fragment);
        return valid;
    }
}
