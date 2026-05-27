// VersionFilterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Packaging;

#endregion

namespace SaddleRAG.Tests.Packaging;

public sealed class VersionFilterTests
{
    [Fact]
    public void ParseCurrentReturnsCurrentKind()
    {
        var filter = VersionFilter.Parse("current");
        Assert.Equal(VersionFilterKind.Current, filter.Kind);
        Assert.Empty(filter.ExplicitVersions);
    }

    [Fact]
    public void ParseAllReturnsAllKind()
    {
        var filter = VersionFilter.Parse("all");
        Assert.Equal(VersionFilterKind.All, filter.Kind);
    }

    [Fact]
    public void ParseExplicitListReturnsExplicitKind()
    {
        var filter = VersionFilter.Parse(new[] { "1.0", "1.1" });
        Assert.Equal(VersionFilterKind.Explicit, filter.Kind);
        Assert.Equal(new[] { "1.0", "1.1" }, filter.ExplicitVersions);
    }

    [Fact]
    public void ResolveCurrentReturnsLibraryCurrentVersion()
    {
        var filter = VersionFilter.Parse("current");
        var available = new[] { "1.0", "1.1", "2.0" };

        var resolved = filter.Resolve(currentVersion: "1.1", availableVersions: available);

        Assert.Equal(new[] { "1.1" }, resolved);
    }

    [Fact]
    public void ResolveAllReturnsEveryAvailableVersion()
    {
        var filter = VersionFilter.Parse("all");
        var available = new[] { "1.0", "1.1", "2.0" };

        var resolved = filter.Resolve(currentVersion: "2.0", availableVersions: available);

        Assert.Equal(available, resolved);
    }

    [Fact]
    public void ResolveExplicitFailsFastIfAnyMissing()
    {
        var filter = VersionFilter.Parse(new[] { "1.0", "9.9" });
        var available = new[] { "1.0", "1.1" };

        var ex = Assert.Throws<ArgumentException>(
            (Action) (() => filter.Resolve(currentVersion: "1.1", availableVersions: available)));

        Assert.Contains("9.9", ex.Message);
    }
}
