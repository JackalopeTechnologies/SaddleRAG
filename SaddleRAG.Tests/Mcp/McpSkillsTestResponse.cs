// McpSkillsTestResponse.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using System.Text.Json.Nodes;

#endregion

namespace SaddleRAG.Tests.Mcp;

internal sealed record McpSkillsTestResponse(HttpStatusCode StatusCode, JsonObject? Message, bool HasSessionId);
