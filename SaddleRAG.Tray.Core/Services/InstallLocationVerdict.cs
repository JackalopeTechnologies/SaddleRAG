// InstallLocationVerdict.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>Whether the folder holding the elevated script is protected from non-administrators.</summary>
/// <param name="IsSafe">True when only SYSTEM, Administrators and TrustedInstaller can change the script.</param>
/// <param name="Reason">Why it is unsafe (names the path and principal); empty when safe.</param>
public sealed record InstallLocationVerdict(bool IsSafe, string Reason)
{
    public static InstallLocationVerdict Safe { get; } = new(IsSafe: true, string.Empty);

    public static InstallLocationVerdict Unsafe(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new InstallLocationVerdict(IsSafe: false, reason);
    }
}
