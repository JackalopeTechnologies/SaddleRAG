// AclEntry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     One access-control entry reduced to what the install-location check reads. A separate type
///     because <see cref="FileSystemAccessRule" /> refuses to be built with the generic rights bits
///     (GENERIC_WRITE, GENERIC_ALL) that real entries on disk can carry, so a test could not
///     otherwise express them.
/// </summary>
/// <param name="Sid">The principal the entry names.</param>
/// <param name="Rights">The entry's access mask.</param>
/// <param name="Type">Allow or Deny.</param>
/// <param name="Propagation">Whether the entry is inherit-only, so it does not govern the object itself.</param>
[SupportedOSPlatform("windows")]
internal readonly record struct AclEntry(SecurityIdentifier Sid,
                                          FileSystemRights Rights,
                                          AccessControlType Type,
                                          PropagationFlags Propagation);
