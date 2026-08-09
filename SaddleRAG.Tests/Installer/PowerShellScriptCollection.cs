// PowerShellScriptCollection.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tests.Installer;

/// <summary>
///     Groups the tests that spawn a real Windows PowerShell 5.1 process to drive
///     <c>PatchAppSettings.ps1</c>. Parallelization is disabled for the group: each
///     case pays a full interpreter start-up, and running several at once on a loaded
///     machine pushed them past their exit budget and produced false failures rather
///     than finding real script defects.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PowerShellScriptCollection
{
    public const string Name = "PowerShell script execution";
}
