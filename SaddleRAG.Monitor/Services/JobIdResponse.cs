// JobIdResponse.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Wire shape for endpoints that return <c>{ "JobId": "..." }</c>.
/// </summary>
internal sealed record JobIdResponse(string JobId);
