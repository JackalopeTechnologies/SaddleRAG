// DiagnosticsWriteHandler.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings
using Microsoft.AspNetCore.Authorization;
#endregion

namespace SaddleRAG.Mcp.Auth;

public sealed class DiagnosticsWriteHandler : AuthorizationHandler<DiagnosticsWriteRequirement>
{
    /// <summary>
    ///     Initializes a new instance of <see cref="DiagnosticsWriteHandler"/>.
    /// </summary>
    public DiagnosticsWriteHandler(IConfiguration configuration)
    {
        mToken = configuration[DiagnosticsConfigKey];
    }

    private readonly string? mToken;

    private const string DiagnosticsConfigKey = "Diagnostics:WriteToken";
    private const string BearerPrefix          = "Bearer ";

    /// <inheritdoc/>
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
                                                    DiagnosticsWriteRequirement requirement)
    {
        bool succeeded = DetermineSuccess(context);
        if (succeeded)
            context.Succeed(requirement);
        else
            context.Fail();
        return Task.CompletedTask;
    }

    private bool DetermineSuccess(AuthorizationHandlerContext context) =>
        string.IsNullOrEmpty(mToken) || IsValidBearer(context);

    private bool IsValidBearer(AuthorizationHandlerContext context)
    {
        bool result = false;
        if (context.Resource is HttpContext http)
        {
            var authHeader = http.Request.Headers.Authorization.FirstOrDefault();
            result = string.Equals(authHeader, BearerPrefix + mToken, StringComparison.Ordinal);
        }
        return result;
    }
}
