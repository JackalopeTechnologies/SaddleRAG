// ClientRegistrarTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class ClientRegistrarTests
{
    [Fact]
    public async Task RegisterAllRunsEveryWriter()
    {
        var w1 = new FakeWriter("alpha", succeed: true);
        var w2 = new FakeWriter("bravo", succeed: true);
        var registrar = new ClientRegistrar([w1, w2]);

        var result = await registrar.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.True(result.AllRegisterSucceeded);
        Assert.Equal(expected: 2, result.RegisterResults.Count);
        Assert.Equal(expected: 1, w1.RegisterCallCount);
        Assert.Equal(expected: 1, w2.RegisterCallCount);
    }

    [Fact]
    public async Task RegisterFailureDoesNotStopOtherWriters()
    {
        var w1 = new FakeWriter("alpha", succeed: false);
        var w2 = new FakeWriter("bravo", succeed: true);
        var registrar = new ClientRegistrar([w1, w2]);

        var result = await registrar.RegisterAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.False(result.AllRegisterSucceeded);
        Assert.False(result.RegisterResults[index: 0].Success);
        Assert.True(result.RegisterResults[index: 1].Success);
        Assert.Equal(expected: 1, w2.RegisterCallCount);
    }

    [Fact]
    public async Task UnregisterAllRunsEveryWriter()
    {
        var w1 = new FakeWriter("alpha", succeed: true);
        var w2 = new FakeWriter("bravo", succeed: true);
        var registrar = new ClientRegistrar([w1, w2]);

        var result = await registrar.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.True(result.AllUnregisterSucceeded);
        Assert.Equal(expected: 2, result.UnregisterResults.Count);
    }

    [Fact]
    public async Task RegisterPassesEndpointToEveryWriter()
    {
        var w1 = new FakeWriter("alpha", succeed: true);
        var endpoint = new SaddleRagEndpoint("http://test:1234/mcp", TimeoutSeconds: 60, []);
        var registrar = new ClientRegistrar([w1]);

        await registrar.RegisterAsync(endpoint, TestContext.Current.CancellationToken);

        Assert.Equal(endpoint, w1.LastEndpoint);
    }

    private sealed class FakeWriter : IClientWriter
    {
        private readonly bool mSucceed;

        public FakeWriter(string name, bool succeed)
        {
            ClientName = name;
            mSucceed = succeed;
        }

        public string ClientName { get; }
        public int RegisterCallCount { get; private set; }
        public SaddleRagEndpoint? LastEndpoint { get; private set; }

        public Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct)
        {
            RegisterCallCount++;
            LastEndpoint = endpoint;
            RegisterResult res = mSucceed
                                     ? RegisterResult.Ok(ClientName, "fake-path", "ok")
                                     : RegisterResult.Failed(ClientName, "fake-path", "boom");
            return Task.FromResult(res);
        }

        public Task<UnregisterResult> UnregisterAsync(CancellationToken ct)
            => Task.FromResult(mSucceed
                                   ? UnregisterResult.Removed(ClientName, "fake-path", "ok")
                                   : UnregisterResult.Failed(ClientName, "fake-path", "boom"));

        public Task<StatusResult> GetStatusAsync(CancellationToken ct)
            => Task.FromResult(new StatusResult(ClientName, "fake-path", ConfigFileExists: false, SaddleRagEntryPresent: false, EndpointMatchesCanonical: false, SkillFilePresent: null, "fake"));
    }
}
