// DiagnosticsWriteRequirement.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.AspNetCore.Authorization;

#endregion

namespace SaddleRAG.Mcp.Auth;

public sealed class DiagnosticsWriteRequirement : IAuthorizationRequirement
{
    public const string PolicyName = "DiagnosticsWrite";
}
