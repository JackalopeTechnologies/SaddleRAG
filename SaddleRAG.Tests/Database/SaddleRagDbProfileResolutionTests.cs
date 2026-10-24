// SaddleRagDbProfileResolutionTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Database;

namespace SaddleRAG.Tests.Database;

public sealed class SaddleRagDbProfileResolutionTests
{
    [Theory]
    [InlineData(Local, null, Local)]
    [InlineData(Local, Shared, Shared)]
    [InlineData(null, Shared, Shared)]
    [InlineData("missing", Local, Local)]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("missing", null, null)]
    [InlineData(Local, "", null)]
    [InlineData(Local, "missing", null)]
    [InlineData(Local, "LOCAL", null)]
    public void CacheAliasAndDatabaseSelectionFollowTheSameConfiguredName(
        string? activeProfile, string? profileOverride, string? expectedProfile)
    {
        var settings = new SaddleRagDbSettings
                           {
                               ActiveProfile = activeProfile,
                               ConnectionString = DirectConnection,
                               DatabaseName = DirectDatabase,
                               Profiles = new Dictionary<string, MongoDbProfile>
                                              {
                                                  [Local] = new()
                                                      {
                                                          ConnectionString = LocalConnection,
                                                          DatabaseName = LocalDatabase
                                                      },
                                                  [Shared] = new()
                                                      {
                                                          ConnectionString = SharedConnection,
                                                          DatabaseName = SharedDatabase
                                                      }
                                              }
                           };
        (string connection, string database) = expectedProfile switch
            {
                Local => (LocalConnection, LocalDatabase),
                Shared => (SharedConnection, SharedDatabase),
                _ => (DirectConnection, DirectDatabase)
            };

        Assert.Equal(expectedProfile, settings.ResolveConfiguredProfileName(profileOverride));
        Assert.Equal((connection, database), settings.Resolve(profileOverride));
    }

    private const string Local = "local";
    private const string Shared = "shared";
    private const string DirectConnection = "mongodb://direct.example.test";
    private const string DirectDatabase = "DirectCorpus";
    private const string LocalConnection = "mongodb://local.example.test";
    private const string LocalDatabase = "LocalCorpus";
    private const string SharedConnection = "mongodb://shared.example.test";
    private const string SharedDatabase = "SharedCorpus";
}
