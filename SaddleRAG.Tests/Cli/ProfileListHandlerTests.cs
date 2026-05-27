// ProfileListHandlerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Cli.Handlers;
using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Tests.Cli;

/// <summary>
///     Verifies the saddlerag-cli profile list command's resolution +
///     marker rendering. Pins three things: (a) env-var override beats
///     settings.ActiveProfile, (b) the active profile gets the marker,
///     (c) the no-profiles-defined fallback message is shown.
/// </summary>
public sealed class ProfileListHandlerTests
{
    [Fact]
    public void RunReportsDirectFallbackLabelWhenNoActiveProfileAndNoEnvOverride()
    {
        var settings = new SaddleRagDbSettings
            {
                ActiveProfile = null,
                ConnectionString = "mongodb://localhost:27017",
                DatabaseName = "saddlerag"
            };
        var output = new StringWriter();

        var exit = ProfileListHandler.Run(settings, profileEnvOverride: null, output);

        Assert.Equal(0, exit);
        Assert.Contains("Active: (direct)", output.ToString());
        Assert.Contains("No profiles defined.", output.ToString());
    }

    [Fact]
    public void RunMarksProfileWithActiveMarkerWhenEnvOverrideMatchesProfileName()
    {
        var settings = new SaddleRagDbSettings
            {
                ActiveProfile = "local",
                Profiles = new Dictionary<string, MongoDbProfile>
                               {
                                   ["local"] = new()
                                                   {
                                                       ConnectionString = "mongodb://localhost:27017",
                                                       DatabaseName = "saddlerag-local"
                                                   },
                                   ["company"] = new()
                                                     {
                                                         ConnectionString = "mongodb://shared:27017",
                                                         DatabaseName = "saddlerag-company"
                                                     }
                               }
            };
        var output = new StringWriter();

        var exit = ProfileListHandler.Run(settings, profileEnvOverride: "company", output);

        Assert.Equal(0, exit);
        var rendered = output.ToString();
        // env override beats settings.ActiveProfile; "company" should be marked active, "local" should not.
        Assert.Contains("Active: company", rendered);
        var companyLine = rendered.Split('\n').FirstOrDefault(l => l.Contains("company:"));
        var localLine = rendered.Split('\n').FirstOrDefault(l => l.Contains("local:"));
        Assert.NotNull(companyLine);
        Assert.NotNull(localLine);
        Assert.Contains(ProfileListHandler.ActiveMarker, companyLine);
        Assert.DoesNotContain(ProfileListHandler.ActiveMarker, localLine);
    }

    [Fact]
    public void RunFallsBackToSettingsActiveProfileWhenEnvOverrideIsNull()
    {
        var settings = new SaddleRagDbSettings
            {
                ActiveProfile = "local",
                Profiles = new Dictionary<string, MongoDbProfile>
                               {
                                   ["local"] = new()
                                                   {
                                                       ConnectionString = "mongodb://localhost:27017",
                                                       DatabaseName = "saddlerag-local"
                                                   }
                               }
            };
        var output = new StringWriter();

        var exit = ProfileListHandler.Run(settings, profileEnvOverride: null, output);

        Assert.Equal(0, exit);
        Assert.Contains("Active: local", output.ToString());
    }
}
