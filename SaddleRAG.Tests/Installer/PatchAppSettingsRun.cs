// PatchAppSettingsRun.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tests.Installer;

internal sealed record PatchAppSettingsRun(string RepositoryRoot,
                                           string AppSettingsPath,
                                           int ExitCode,
                                           string StandardOutput,
                                           string StandardError);
