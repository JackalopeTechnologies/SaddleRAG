// DependencyHealth.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json.Serialization;

namespace SaddleRAG.Core.Models;

/// <summary>
///     A cached service observation, independent of the database it describes.
///     <paramref name="HostedLocally" /> says whether the service runs on this computer (true), on another
///     computer (false), or is not a located service such as the monitor's own entry (null, omitted from JSON).
/// </summary>
public sealed record DependencyHealth(string Name, string Status, bool Required, string AffectedWork,
                                      string Detail, string Remediation, DateTimeOffset? LastCheckedAt,
                                      DateTimeOffset? LastSucceededAt = null, DateTimeOffset? UnavailableSince = null,
                                      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                      bool? HostedLocally = null);
