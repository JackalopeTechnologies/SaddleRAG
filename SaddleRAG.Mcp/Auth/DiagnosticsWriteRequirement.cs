// DiagnosticsWriteRequirement.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.AspNetCore.Authorization;

#endregion

namespace SaddleRAG.Mcp.Auth;

public sealed class DiagnosticsWriteRequirement : IAuthorizationRequirement
{
    public const string PolicyName = "DiagnosticsWrite";
}
