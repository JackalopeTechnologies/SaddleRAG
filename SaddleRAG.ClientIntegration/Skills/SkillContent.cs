// SkillContent.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.ClientIntegration.Skills;

/// <summary>
///     One SaddleRAG skill document (name, one-line description, and body) parsed from
///     its embedded markdown resource.
/// </summary>
public sealed record SkillContent(string Name, string Description, string Body);
