// MonitorDoclingLaunchRequestProbeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using System.Text;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

/// <summary>
///     Pins how the tray reads the Docling launch request when the Monitor cannot answer
///     normally. While the database is down the Monitor replies 503 with a short JSON
///     message; the tray must treat that as "no launch request" and carry on quietly.
/// </summary>
public sealed class MonitorDoclingLaunchRequestProbeTests
{
    [Fact]
    public async Task ADatabaseOutageReplyMeansNoLaunchRequestAndDoesNotThrow()
    {
        using var client = CreateClient(HttpStatusCode.ServiceUnavailable, OutageBody);
        var probe = new MonitorDoclingLaunchRequestProbe(client);

        bool requested = await probe.IsLaunchRequestedAsync(TestContext.Current.CancellationToken);

        Assert.False(requested);
    }

    [Fact]
    public async Task ASuccessfulReplyWithTheFlagSetMeansLaunchRequested()
    {
        using var client = CreateClient(HttpStatusCode.OK, RequestedBody);
        var probe = new MonitorDoclingLaunchRequestProbe(client);

        bool requested = await probe.IsLaunchRequestedAsync(TestContext.Current.CancellationToken);

        Assert.True(requested);
    }

    private static HttpClient CreateClient(HttpStatusCode status, string body) =>
        new HttpClient(new StubHandler(status, body));

    private const string OutageBody = "{ \"error\": \"SaddleRAG's database at localhost:27017 is unreachable.\" }";
    private const string RequestedBody = "{ \"launchRequested\": true, \"reasonCode\": \"x\" }";

    /// <summary>Answers every request with one fixed status and JSON body.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
                               {
                                   Content = new StringContent(body, Encoding.UTF8, "application/json")
                               };
            return Task.FromResult(response);
        }
    }
}
