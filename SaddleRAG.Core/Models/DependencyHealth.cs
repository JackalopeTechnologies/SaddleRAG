// DependencyHealth.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>A cached service observation, independent of the database it describes.</summary>
public sealed record DependencyHealth(string Name, string Status, bool Required, string AffectedWork,
                                      string Detail, string Remediation, DateTimeOffset? LastCheckedAt,
                                      DateTimeOffset? LastSucceededAt = null, DateTimeOffset? UnavailableSince = null);
