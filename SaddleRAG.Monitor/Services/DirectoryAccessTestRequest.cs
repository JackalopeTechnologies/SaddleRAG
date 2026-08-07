// DirectoryAccessTestRequest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Monitor.Services;

/// <summary>Visible user-selected directory to validate without registering or scanning it.</summary>
public sealed record DirectoryAccessTestRequest(string RootPath, bool Recursive);
