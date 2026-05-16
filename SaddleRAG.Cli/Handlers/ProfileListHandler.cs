// ProfileListHandler.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Cli.Handlers;

/// <summary>
///     Renders the output of <c>saddlerag-cli profile list</c>. Resolves
///     the effective active profile (env-var override beats settings),
///     prints the connection string + database name in use, then lists
///     every configured profile with a (* active) marker. Extracted from
///     Program.cs so the resolution logic + marker-rendering can be unit-
///     tested.
/// </summary>
public static class ProfileListHandler
{
    /// <summary>
    ///     Run the profile-list command. <paramref name="profileEnvOverride" />
    ///     is the resolved value of <c>SADDLERAG_MONGODB_PROFILE</c> at the
    ///     call site (so tests can pass whatever they want without touching
    ///     real process env vars). Returns 0 on success.
    /// </summary>
    public static int Run(SaddleRagDbSettings settings, string? profileEnvOverride, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(output);

        (var activeConn, var activeDb) = settings.Resolve();
        var activeProfileName = profileEnvOverride ?? settings.ActiveProfile ?? DirectLabel;

        output.WriteLine($"Active: {activeProfileName}");
        output.WriteLine($"Connected to: {activeConn} / {activeDb}");
        output.WriteLine();

        if (settings.Profiles.Count == 0)
            output.WriteLine("No profiles defined. Using direct ConnectionString/DatabaseName.");
        else
        {
            foreach((var name, var profile) in settings.Profiles)
            {
                var isActive = name.Equals(profileEnvOverride ?? settings.ActiveProfile,
                                           StringComparison.OrdinalIgnoreCase
                                          );
                var marker = isActive ? ActiveMarker : string.Empty;
                output.WriteLine($"  {name}: {profile.ConnectionString} / {profile.DatabaseName}{marker}");
                if (!string.IsNullOrEmpty(profile.Description))
                    output.WriteLine($"    {profile.Description}");
            }
        }

        output.WriteLine();
        output.WriteLine("Switch profiles:");
        output.WriteLine("  Set SADDLERAG_MONGODB_PROFILE=company  (environment variable)");
        output.WriteLine("  Or edit ActiveProfile in appsettings.json");

        return 0;
    }

    internal const string ActiveMarker = " ←";
    internal const string DirectLabel = "(direct)";
}
