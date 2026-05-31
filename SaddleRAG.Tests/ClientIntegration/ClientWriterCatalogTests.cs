// ClientWriterCatalogTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.ClientIntegration;
using Xunit;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class ClientWriterCatalogTests
{
    private const int ExpectedAgentCount = 9;
    private const string AllKey = "all";
    private const string UnknownKey = "nope-not-real";

    [Fact]
    public void AllHasExactlyNineDescriptors()
    {
        Assert.Equal(ExpectedAgentCount, ClientWriterCatalog.All.Count);
    }

    [Fact]
    public void AllKeysAreUnique()
    {
        IEnumerable<string> keys = ClientWriterCatalog.All.Select(d => d.Key);
        int distinct = keys.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(ExpectedAgentCount, distinct);
    }

    [Fact]
    public void EveryKeyMatchesItsWriterClientName()
    {
        foreach (ClientWriterCatalog.ClientDescriptor descriptor in ClientWriterCatalog.All)
        {
            IClientWriter writer = descriptor.Factory();
            Assert.Equal(descriptor.Key, writer.ClientName);
        }
    }

    [Fact]
    public void EveryDescriptorHasNonEmptyDisplayName()
    {
        foreach (ClientWriterCatalog.ClientDescriptor descriptor in ClientWriterCatalog.All)
            Assert.False(string.IsNullOrWhiteSpace(descriptor.DisplayName));
    }

    [Fact]
    public void FindByKeyResolvesEveryCatalogKey()
    {
        foreach (ClientWriterCatalog.ClientDescriptor descriptor in ClientWriterCatalog.All)
        {
            IClientWriter? writer = ClientWriterCatalog.FindByKey(descriptor.Key);
            Assert.NotNull(writer);
            Assert.Equal(descriptor.Key, writer.ClientName);
        }
    }

    [Fact]
    public void FindByKeyIsCaseInsensitive()
    {
        IClientWriter? lower = ClientWriterCatalog.FindByKey("cursor");
        IClientWriter? mixed = ClientWriterCatalog.FindByKey("Cursor");
        Assert.NotNull(lower);
        Assert.NotNull(mixed);
        Assert.Equal(lower.ClientName, mixed.ClientName);
    }

    [Fact]
    public void FindByKeyReturnsNullForUnknownKey()
    {
        IClientWriter? writer = ClientWriterCatalog.FindByKey(UnknownKey);
        Assert.Null(writer);
    }

    [Fact]
    public void AllIsNotACatalogKey()
    {
        bool present = ClientWriterCatalog.All.Any(d => string.Equals(d.Key, AllKey, StringComparison.OrdinalIgnoreCase));
        Assert.False(present);
        Assert.Null(ClientWriterCatalog.FindByKey(AllKey));
    }
}
