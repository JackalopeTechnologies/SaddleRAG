// VectorProfileAliasTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Ingestion.Embedding;

namespace SaddleRAG.Tests.Embedding;

public sealed class VectorProfileAliasTests
{
    [Theory]
    [InlineData(null, Local)]
    [InlineData("", Local)]
    [InlineData(Local, null)]
    [InlineData(Local, "")]
    public async Task IndexingEitherAliasSupportsExactLibraryAndCrossLibrarySearch(
        string? indexedProfile, string? searchProfile)
    {
        var provider = CreateProvider();
        await IndexAsync(provider, indexedProfile, Library, Version, FirstChunk);
        await IndexAsync(provider, indexedProfile, OtherLibrary, Version, OtherChunk);

        Assert.Equal(FirstChunk, Assert.Single(await SearchAsync(provider, searchProfile, Library, Version)).Chunk.Id);
        Assert.Equal(FirstChunk, Assert.Single(await SearchAsync(provider, searchProfile, Library)).Chunk.Id);
        Assert.Equal([FirstChunk, OtherChunk],
                     (await SearchAsync(provider, searchProfile)).Select(result => result.Chunk.Id).Order().ToArray());
    }

    [Theory]
    [InlineData(null, Local)]
    [InlineData("", Local)]
    [InlineData(Local, null)]
    [InlineData(Local, "")]
    public async Task ReplacingThroughEitherAliasUpdatesTheSharedIndex(
        string? firstProfile, string? replacementProfile)
    {
        var provider = CreateProvider();
        await IndexAsync(provider, firstProfile, Library, Version, FirstChunk);
        await IndexAsync(provider, replacementProfile, Library, Version, ReplacementChunk);

        Assert.Equal(ReplacementChunk, Assert.Single(await SearchAsync(provider, null)).Chunk.Id);
        Assert.Equal(ReplacementChunk, Assert.Single(await SearchAsync(provider, string.Empty)).Chunk.Id);
        Assert.Equal(ReplacementChunk, Assert.Single(await SearchAsync(provider, Local)).Chunk.Id);
    }

    [Theory]
    [InlineData(null, Local)]
    [InlineData("", Local)]
    [InlineData(Local, null)]
    [InlineData(Local, "")]
    public async Task RemovingVersionThroughEitherAliasPreservesOtherVersionsLibrariesAndProfiles(
        string? indexedProfile, string? removalProfile)
    {
        var provider = CreateProvider();
        await IndexRemovalFixturesAsync(provider, indexedProfile);

        await provider.RemoveIndexAsync(removalProfile, Library, Version, TestContext.Current.CancellationToken);

        Assert.Empty(await SearchAsync(provider, indexedProfile, Library, Version));
        Assert.Equal(OtherVersionChunk,
                     Assert.Single(await SearchAsync(provider, Local, Library, OtherVersion)).Chunk.Id);
        Assert.Equal(OtherChunk, Assert.Single(await SearchAsync(provider, null, OtherLibrary)).Chunk.Id);
        Assert.Equal(OtherProfileChunk, Assert.Single(await SearchAsync(provider, OtherProfile)).Chunk.Id);
    }

    [Theory]
    [InlineData(null, Local)]
    [InlineData("", Local)]
    [InlineData(Local, null)]
    [InlineData(Local, "")]
    public async Task RemovingLibraryThroughEitherAliasPreservesOtherLibrariesAndProfiles(
        string? indexedProfile, string? removalProfile)
    {
        var provider = CreateProvider();
        await IndexRemovalFixturesAsync(provider, indexedProfile);

        await provider.RemoveLibraryIndexesAsync(removalProfile, Library, TestContext.Current.CancellationToken);

        Assert.Empty(await SearchAsync(provider, indexedProfile, Library));
        Assert.Empty(await SearchAsync(provider, Local, Library));
        Assert.Equal(OtherChunk, Assert.Single(await SearchAsync(provider, null)).Chunk.Id);
        Assert.Equal(OtherProfileChunk, Assert.Single(await SearchAsync(provider, OtherProfile)).Chunk.Id);
    }

    [Fact]
    public async Task DifferentNamedProfilesRemainIsolatedEvenWithIdenticalDatabaseSettings()
    {
        var provider = CreateProvider();
        await IndexAsync(provider, Local, Library, Version, FirstChunk);
        await IndexAsync(provider, OtherProfile, Library, Version, OtherProfileChunk);

        Assert.Equal(FirstChunk, Assert.Single(await SearchAsync(provider, Local)).Chunk.Id);
        Assert.Equal(OtherProfileChunk, Assert.Single(await SearchAsync(provider, OtherProfile)).Chunk.Id);
    }

    [Fact]
    public async Task ParameterlessConstructionRetainsIndependentDefaultAndNamedProfiles()
    {
        var provider = new InMemoryBruteForceVectorSearch();
        await IndexAsync(provider, null, Library, Version, FirstChunk);
        await IndexAsync(provider, Local, Library, Version, OtherProfileChunk);

        Assert.Equal(FirstChunk, Assert.Single(await SearchAsync(provider, string.Empty)).Chunk.Id);
        Assert.Equal(OtherProfileChunk, Assert.Single(await SearchAsync(provider, Local)).Chunk.Id);
    }

    [Fact]
    public async Task NormalRegistrationUsesTheResolvedDefaultProfileAlias()
    {
        SaddleRagDbSettings settings = CreateSettings();
        using ServiceProvider services = new ServiceCollection()
                                         .AddSingleton<IOptions<SaddleRagDbSettings>>(Options.Create(settings))
                                         .AddSingleton<IVectorSearchProvider, InMemoryBruteForceVectorSearch>()
                                         .BuildServiceProvider();
        var provider = Assert.IsType<InMemoryBruteForceVectorSearch>(
            services.GetRequiredService<IVectorSearchProvider>());
        await IndexAsync(provider, null, Library, Version, FirstChunk);

        Assert.Equal(FirstChunk,
                     Assert.Single(await SearchAsync(provider, settings.ResolveConfiguredProfileName())).Chunk.Id);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("missing", null, null)]
    [InlineData(Local, "", null)]
    [InlineData(Local, "missing", null)]
    [InlineData(Local, OtherProfile, OtherProfile)]
    [InlineData(null, OtherProfile, OtherProfile)]
    [InlineData("missing", Local, Local)]
    public async Task ConfiguredSelectionAndOverridePreserveDirectAndOtherProfileIsolation(
        string? activeProfile, string? profileOverride, string? expectedAlias)
    {
        SaddleRagDbSettings settings = CreateSettings();
        settings.ActiveProfile = activeProfile;
        string? effectiveProfile = settings.ResolveConfiguredProfileName(profileOverride);
        Assert.Equal(expectedAlias, effectiveProfile);
        var provider = new InMemoryBruteForceVectorSearch(effectiveProfile);
        string separateProfile = expectedAlias == Local ? OtherProfile : Local;
        await IndexAsync(provider, null, Library, Version, FirstChunk);
        await IndexAsync(provider, separateProfile, Library, Version, OtherProfileChunk);

        Assert.Equal(FirstChunk, Assert.Single(await SearchAsync(provider, expectedAlias)).Chunk.Id);
        Assert.Equal(FirstChunk, Assert.Single(await SearchAsync(provider, string.Empty)).Chunk.Id);
        Assert.Equal(OtherProfileChunk, Assert.Single(await SearchAsync(provider, separateProfile)).Chunk.Id);
    }

    private static InMemoryBruteForceVectorSearch CreateProvider()
    {
        SaddleRagDbSettings settings = CreateSettings();
        return new InMemoryBruteForceVectorSearch(settings.ResolveConfiguredProfileName(profileOverride: null));
    }

    private static SaddleRagDbSettings CreateSettings() => new()
        {
            ActiveProfile = Local,
            Profiles = new Dictionary<string, MongoDbProfile>
                           {
                               [Local] = new(),
                               [OtherProfile] = new()
                           }
        };

    private static async Task IndexRemovalFixturesAsync(InMemoryBruteForceVectorSearch provider, string? profile)
    {
        await IndexAsync(provider, profile, Library, Version, FirstChunk);
        await IndexAsync(provider, profile, Library, OtherVersion, OtherVersionChunk);
        await IndexAsync(provider, profile, OtherLibrary, Version, OtherChunk);
        await IndexAsync(provider, OtherProfile, Library, Version, OtherProfileChunk);
    }

    private static Task IndexAsync(InMemoryBruteForceVectorSearch provider,
                                   string? profile,
                                   string library,
                                   string version,
                                   string chunkId) =>
        provider.IndexChunksAsync(profile,
                                   library,
                                   version,
                                   [new DocChunk
                                        {
                                            Id = chunkId,
                                            LibraryId = library,
                                            Version = version,
                                            PageUrl = $"https://example.test/{chunkId}",
                                            PageTitle = chunkId,
                                            Category = DocCategory.HowTo,
                                            Content = chunkId,
                                            TokenCount = 1,
                                            Embedding = [1.0f, 0.0f]
                                        }],
                                   TestContext.Current.CancellationToken);

    private static Task<IReadOnlyList<VectorSearchResult>> SearchAsync(InMemoryBruteForceVectorSearch provider,
                                                                       string? profile,
                                                                       string? library = null,
                                                                       string? version = null) =>
        provider.SearchAsync([1.0f, 0.0f],
                              new VectorSearchFilter { Profile = profile, LibraryId = library, Version = version },
                              10,
                              TestContext.Current.CancellationToken);

    private const string Local = "local";
    private const string OtherProfile = "other-profile";
    private const string Library = "library";
    private const string OtherLibrary = "other-library";
    private const string Version = "v1";
    private const string OtherVersion = "v2";
    private const string FirstChunk = "first-chunk";
    private const string ReplacementChunk = "replacement-chunk";
    private const string OtherChunk = "other-chunk";
    private const string OtherVersionChunk = "other-version-chunk";
    private const string OtherProfileChunk = "other-profile-chunk";
}
