// DiagnosticsWriteRequirement.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings
using Microsoft.AspNetCore.Authorization;
#endregion

namespace SaddleRAG.Mcp.Auth;

public sealed class DiagnosticsWriteRequirement : IAuthorizationRequirement
{
    public const string PolicyName = "DiagnosticsWrite";
}
