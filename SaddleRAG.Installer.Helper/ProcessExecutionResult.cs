// ProcessExecutionResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Installer.Helper;

/// <summary>Exit code and independently captured child-process streams.</summary>
public sealed record ProcessExecutionResult(int ExitCode,
                                            string StandardOutput,
                                            string StandardError);
