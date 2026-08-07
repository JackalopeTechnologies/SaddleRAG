// DoclingReadinessProbeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Ingestion.Documents.Docling;

#endregion

namespace SaddleRAG.Tests.Documents.Docling;

public sealed class DoclingReadinessProbeTests
{
    [Fact]
    public async Task TransientConnectionAndModelStartupThenKnownConversionReturnsReady()
    {
        var client = new ScriptedDoclingClient();
        client.EnqueueHealth(
            DoclingServiceObservation.Failure(DoclingReasonCodes.EndpointUnreachable, "Connection refused"),
            DoclingServiceObservation.Success("health ok"),
            DoclingServiceObservation.Success("health ok")
        );
        client.EnqueueReadiness(
            DoclingServiceObservation.Failure(DoclingReasonCodes.ModelsUnavailable, "Models not yet loaded"),
            DoclingServiceObservation.Success("models ready")
        );
        client.EnqueueConversions(SuccessfulConversion());
        var clock = new MutableDoclingTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var probe = MakeProbe(client, clock, graceSeconds: 5);

        var result = await probe.ProbeAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.Equal(DoclingCapabilityState.Ready, result.State);
        Assert.Equal(DoclingReasonCodes.Ready, result.ReasonCode);
        Assert.Equal(expected: 3, client.HealthCalls);
        Assert.Equal(expected: 2, client.ReadinessCalls);
        Assert.Equal(expected: 1, client.ConversionCalls);
    }

    [Fact]
    public async Task ModelsRemainStartingDuringGraceThenBecomeUnavailable()
    {
        var client = new ScriptedDoclingClient();
        client.EnqueueHealth(Enumerable.Repeat(DoclingServiceObservation.Success(), count: 4).ToArray());
        client.EnqueueReadiness(
            Enumerable.Repeat(
                DoclingServiceObservation.Failure(DoclingReasonCodes.ModelsUnavailable, "Models not yet loaded"),
                count: 4).ToArray()
        );
        var clock = new MutableDoclingTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var observedStates = new List<DoclingCapabilityStatus>();
        var probe = MakeProbe(client, clock, graceSeconds: 3, observedStates);

        var result = await probe.ProbeAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.Equal(DoclingCapabilityState.Unavailable, result.State);
        Assert.Equal(DoclingReasonCodes.ModelsUnavailable, result.ReasonCode);
        Assert.Equal(expected: 3, observedStates.Count);
        Assert.All(observedStates, status =>
        {
            Assert.Equal(DoclingCapabilityState.Starting, status.State);
            Assert.Equal(DoclingReasonCodes.Starting, status.ReasonCode);
            Assert.Contains("Models not yet loaded", status.Detail, StringComparison.Ordinal);
        });
        Assert.Equal(expected: 0, client.ConversionCalls);
    }

    [Fact]
    public async Task HealthNeverSucceedsBeforeDeadlineReturnsHealthTimeout()
    {
        var client = new ScriptedDoclingClient();
        client.EnqueueHealth(
            Enumerable.Repeat(
                DoclingServiceObservation.Failure(DoclingReasonCodes.EndpointUnreachable, "Connection refused"),
                count: 3).ToArray()
        );
        var clock = new MutableDoclingTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var probe = MakeProbe(client, clock, graceSeconds: 2);

        var result = await probe.ProbeAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.Equal(DoclingCapabilityState.Unavailable, result.State);
        Assert.Equal(DoclingReasonCodes.HealthTimeout, result.ReasonCode);
        Assert.Contains("Connection refused", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected: 0, client.ReadinessCalls);
    }

    [Fact]
    public async Task SuccessfulHealthAndReadinessWithFailedConversionIsNotReady()
    {
        var client = new ScriptedDoclingClient();
        client.EnqueueHealth(DoclingServiceObservation.Success());
        client.EnqueueReadiness(DoclingServiceObservation.Success());
        client.EnqueueConversions(
            DoclingConversionResult.Failure(DoclingReasonCodes.ConversionFailed, "Known probe conversion failed")
        );
        var clock = new MutableDoclingTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var probe = MakeProbe(client, clock);

        var result = await probe.ProbeAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.Equal(DoclingCapabilityState.Unavailable, result.State);
        Assert.Equal(DoclingReasonCodes.ConversionFailed, result.ReasonCode);
        Assert.NotEqual(DoclingReasonCodes.Ready, result.ReasonCode);
    }

    [Fact]
    public async Task SuccessfulConversionWithoutKnownMarkerIsOutputInvalid()
    {
        var client = new ScriptedDoclingClient();
        client.EnqueueHealth(DoclingServiceObservation.Success());
        client.EnqueueReadiness(DoclingServiceObservation.Success());
        var mapped = new DoclingMappedDocument("probe.pdf",
                                               "unexpected content",
                                               "unexpected content",
                                               "{}",
                                               "{}",
                                               []);
        client.EnqueueConversions(DoclingConversionResult.Success(mapped));
        var clock = new MutableDoclingTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var probe = MakeProbe(client, clock);

        var result = await probe.ProbeAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.Equal(DoclingCapabilityState.Unavailable, result.State);
        Assert.Equal(DoclingReasonCodes.OutputInvalid, result.ReasonCode);
    }

    [Fact]
    public async Task MarkdownEscapedKnownMarkerReturnsReadyWithoutChangingMappedContent()
    {
        const string escapedMarker = @"SADDLERAG\_DOCLING\_PROBE\_2026\_08\_04";
        var client = new ScriptedDoclingClient();
        client.EnqueueHealth(DoclingServiceObservation.Success());
        client.EnqueueReadiness(DoclingServiceObservation.Success());
        var mapped = new DoclingMappedDocument("probe.pdf",
                                               escapedMarker,
                                               escapedMarker,
                                               "{\"raw\":\"unchanged\"}",
                                               "{\"raw\":\"unchanged\"}",
                                               []);
        client.EnqueueConversions(DoclingConversionResult.Success(mapped));
        var clock = new MutableDoclingTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var probe = MakeProbe(client, clock);

        var result = await probe.ProbeAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.Equal(DoclingCapabilityState.Ready, result.State);
        Assert.Equal(DoclingReasonCodes.Ready, result.ReasonCode);
        Assert.Equal(escapedMarker, mapped.MarkdownContent);
        Assert.Equal("{\"raw\":\"unchanged\"}", mapped.RawResponseJson);
        Assert.Equal("{\"raw\":\"unchanged\"}", mapped.RawDocumentJson);
    }

    [Fact]
    public async Task CachedCapabilityReadDoesNotContactDoclingUntilExplicitRefresh()
    {
        var client = new ScriptedDoclingClient();
        client.EnqueueHealth(DoclingServiceObservation.Success());
        client.EnqueueReadiness(DoclingServiceObservation.Success());
        client.EnqueueConversions(SuccessfulConversion());
        var clock = new MutableDoclingTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var service = new DoclingCapabilityService(MakeProbe(client, clock));

        var cached = await service.GetStatusAsync(refresh: false, TestContext.Current.CancellationToken);
        var refreshed = await service.GetStatusAsync(refresh: true, TestContext.Current.CancellationToken);

        Assert.Equal(DoclingCapabilityState.NotChecked, cached.State);
        Assert.Equal(expected: 0, cached.LastCheckedAt.Ticks);
        Assert.Equal(DoclingCapabilityState.Ready, refreshed.State);
        Assert.Equal(expected: 1, client.HealthCalls);
        Assert.Equal(refreshed, service.CurrentStatus);
    }

    [Theory]
    [InlineData("DOCLING_UNAUTHORIZED")]
    [InlineData("DOCLING_API_INCOMPATIBLE")]
    [InlineData("DOCLING_HEALTH_INVALID")]
    public async Task NonTransientHealthFailureReturnsItsStableCode(string reasonCode)
    {
        var client = new ScriptedDoclingClient();
        client.EnqueueHealth(DoclingServiceObservation.Failure(reasonCode, "actionable detail"));
        var clock = new MutableDoclingTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var probe = MakeProbe(client, clock);

        var result = await probe.ProbeAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.Equal(DoclingCapabilityState.Unavailable, result.State);
        Assert.Equal(reasonCode, result.ReasonCode);
        Assert.Equal(expected: 1, client.HealthCalls);
    }

    [Fact]
    public async Task InvalidEndpointReturnsWithoutCallingClient()
    {
        var client = new ScriptedDoclingClient();
        var clock = new MutableDoclingTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var settings = new DoclingSettings { Endpoint = "not a URI" };
        var probe = MakeProbe(client, clock, settings: settings);

        var result = await probe.ProbeAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.Equal(DoclingReasonCodes.InvalidEndpoint, result.ReasonCode);
        Assert.Equal(expected: 0, client.HealthCalls);
        Assert.DoesNotContain("not a URI", result.Endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalCancellationIsRethrown()
    {
        var client = new ScriptedDoclingClient();
        client.EnqueueHealth(DoclingServiceObservation.Success());
        var clock = new MutableDoclingTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var probe = MakeProbe(client, clock);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.ProbeAsync(ProbeFile(), cancellation.Token)
        );
    }

    [Fact]
    public async Task DefaultProbeUsesOwnedPdfMarkerDocument()
    {
        var client = new ScriptedDoclingClient();
        client.EnqueueHealth(DoclingServiceObservation.Success());
        client.EnqueueReadiness(DoclingServiceObservation.Success());
        client.EnqueueConversions(SuccessfulConversion());
        var clock = new MutableDoclingTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var probe = MakeProbe(client, clock);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoclingReasonCodes.Ready, result.ReasonCode);
        var convertedFile = Assert.IsType<DoclingFile>(client.LastConvertedFile);
        Assert.Equal("saddlerag-docling-probe.pdf", convertedFile.FileName);
        Assert.Contains(DoclingProbeDocument.Marker,
                        System.Text.Encoding.ASCII.GetString(convertedFile.Content.Span),
                        StringComparison.Ordinal);
    }

    private static DoclingReadinessProbe MakeProbe(ScriptedDoclingClient client,
                                                   MutableDoclingTimeProvider clock,
                                                   int graceSeconds = 120,
                                                   List<DoclingCapabilityStatus>? observations = null,
                                                   DoclingSettings? settings = null)
    {
        var configuredSettings = settings ?? new DoclingSettings
                                             {
                                                 StartupGracePeriodSeconds = graceSeconds,
                                                 StartupPollIntervalMilliseconds = 1000
                                             };
        DoclingReadinessProbe? probe = null;
        probe = new DoclingReadinessProbe(client,
                                          configuredSettings,
                                          clock,
                                          (delay, cancellationToken) =>
                                          {
                                              cancellationToken.ThrowIfCancellationRequested();
                                              if (observations != null && probe != null)
                                                  observations.Add(probe.CurrentStatus);
                                              clock.Advance(delay);
                                              return Task.CompletedTask;
                                          });
        return probe;
    }

    private static DoclingConversionResult SuccessfulConversion() =>
        new DoclingDocumentMapper().Map(DoclingTestSupport.LoadFixture("docling-v1-pdf-success.json"));

    private static DoclingFile ProbeFile() =>
        new("probe.pdf", "application/pdf", new byte[] { 37, 80, 68, 70, 45, 49, 46, 55 });

}
