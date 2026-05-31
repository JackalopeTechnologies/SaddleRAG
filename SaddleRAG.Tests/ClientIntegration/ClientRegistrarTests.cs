// ClientRegistrarTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

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

    [Fact]
    public async Task RegisterDetectedUndetectedAgentIsSkippedAndCountsAsSuccess()
    {
        var undetected = new FakeWriter("alpha", succeed: true, detected: false);
        var registrar = new ClientRegistrar([undetected]);

        var result = await registrar.RegisterDetectedAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        RegisterResult only = Assert.Single(result.RegisterResults);
        Assert.True(only.Skipped);
        Assert.True(only.Success);
        Assert.True(result.AllRegisterSucceeded);
        Assert.Equal(expected: 0, undetected.RegisterCallCount);
    }

    [Fact]
    public async Task RegisterDetectedDetectedAgentIsRegistered()
    {
        var detected = new FakeWriter("alpha", succeed: true, detected: true);
        var registrar = new ClientRegistrar([detected]);

        var result = await registrar.RegisterDetectedAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        RegisterResult only = Assert.Single(result.RegisterResults);
        Assert.False(only.Skipped);
        Assert.True(only.Success);
        Assert.Equal(expected: 1, detected.RegisterCallCount);
    }

    [Fact]
    public async Task RegisterDetectedReportsSkippedRegisteredAndFailedBestEffort()
    {
        var skipped = new FakeWriter("missing", succeed: true, detected: false);
        var okWriter = new FakeWriter("ok", succeed: true, detected: true);
        var throwing = new FakeWriter("boom", succeed: true, detected: true, throwOnRegister: true);
        var registrar = new ClientRegistrar([skipped, okWriter, throwing]);

        var result = await registrar.RegisterDetectedAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.Equal(expected: 3, result.RegisterResults.Count);
        Assert.True(result.RegisterResults.Single(r => r.ClientName == "missing").Skipped);
        Assert.True(result.RegisterResults.Single(r => r.ClientName == "ok").Success);
        Assert.False(result.RegisterResults.Single(r => r.ClientName == "boom").Success);
        Assert.False(result.AllRegisterSucceeded);
    }

    [Fact]
    public async Task RegisterDetectedDetectionThrowsYieldsFailedNotFatal()
    {
        var throwsOnDetect = new FakeWriter("bad", succeed: true, throwOnDetect: true);
        var okWriter = new FakeWriter("ok", succeed: true, detected: true);
        var registrar = new ClientRegistrar([throwsOnDetect, okWriter]);

        var result = await registrar.RegisterDetectedAsync(SaddleRagEndpoint.Default, TestContext.Current.CancellationToken);

        Assert.Equal(expected: 2, result.RegisterResults.Count);
        Assert.False(result.RegisterResults.Single(r => r.ClientName == "bad").Success);
        Assert.True(result.RegisterResults.Single(r => r.ClientName == "ok").Success);
    }

    private sealed class FakeWriter : IClientWriter
    {
        private readonly bool mSucceed;
        private readonly bool mDetected;
        private readonly bool mThrowOnRegister;
        private readonly bool mThrowOnDetect;

        public FakeWriter(
            string name,
            bool succeed,
            bool detected = true,
            bool throwOnRegister = false,
            bool throwOnDetect = false)
        {
            ClientName = name;
            mSucceed = succeed;
            mDetected = detected;
            mThrowOnRegister = throwOnRegister;
            mThrowOnDetect = throwOnDetect;
        }

        public string ClientName { get; }
        public int RegisterCallCount { get; private set; }
        public SaddleRagEndpoint? LastEndpoint { get; private set; }

        public bool IsDetected()
        {
            if (mThrowOnDetect)
                throw new InvalidOperationException("detect-boom");
            return mDetected;
        }

        public Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct)
        {
            if (mThrowOnRegister)
                throw new InvalidOperationException("register-boom");
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
