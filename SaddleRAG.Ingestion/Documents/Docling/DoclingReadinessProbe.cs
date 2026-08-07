// DoclingReadinessProbe.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>
///     Bounded liveness, model-readiness, and known-conversion probe for user-managed Docling.
/// </summary>
public sealed class DoclingReadinessProbe
{
    public DoclingReadinessProbe(IDoclingClient client,
                                 DoclingSettings settings,
                                 TimeProvider? timeProvider = null)
        : this(client,
               settings,
               timeProvider ?? TimeProvider.System,
               static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    internal DoclingReadinessProbe(IDoclingClient client,
                                   DoclingSettings settings,
                                   TimeProvider timeProvider,
                                   Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(delayAsync);

        mClient = client;
        mSettings = settings;
        mTimeProvider = timeProvider;
        mDelayAsync = delayAsync;
    }

    private readonly IDoclingClient mClient;
    private readonly Func<TimeSpan, CancellationToken, Task> mDelayAsync;
    private readonly DoclingSettings mSettings;
    private readonly TimeProvider mTimeProvider;

    public DoclingCapabilityStatus CurrentStatus { get; private set; } =
        new(DoclingCapabilityState.NotChecked,
            DoclingReasonCodes.Starting,
            NotCheckedDetail,
            string.Empty,
            DateTimeOffset.MinValue,
            NotCheckedRemediation);

    public Task<DoclingCapabilityStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
        ProbeAsync(DoclingProbeDocument.CreatePdf(), cancellationToken);

    public async Task<DoclingCapabilityStatus> ProbeAsync(DoclingFile probeFile,
                                                          CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probeFile);
        DoclingCapabilityStatus result;
        var validation = mSettings.Validate();
        if (!validation.IsValid)
        {
            result = CreateStatus(DoclingCapabilityState.Unavailable,
                                  validation.ReasonCode,
                                  validation.Detail,
                                  string.Empty);
            CurrentStatus = result;
        }
        else
        {
            result = await ProbeValidatedAsync(probeFile,
                                               validation,
                                               cancellationToken);
        }

        return result;
    }

    private async Task<DoclingCapabilityStatus> ProbeValidatedAsync(DoclingFile probeFile,
                                                                    DoclingSettingsValidation validation,
                                                                    CancellationToken cancellationToken)
    {
        var endpoint = DoclingEndpointDisplay.Get(validation);
        var deadline = mTimeProvider.GetUtcNow().AddSeconds(mSettings.StartupGracePeriodSeconds);
        var finished = false;
        while (!finished)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var health = await mClient.CheckHealthAsync(cancellationToken);
            (finished, CurrentStatus) = health.Succeeded
                ? await ObserveReadinessAsync(probeFile, endpoint, deadline, cancellationToken)
                : await ObserveHealthFailureAsync(health, endpoint, deadline, cancellationToken);
        }

        return CurrentStatus;
    }

    private async Task<(bool Finished, DoclingCapabilityStatus Status)> ObserveHealthFailureAsync(
        DoclingServiceObservation observation,
        string endpoint,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var now = mTimeProvider.GetUtcNow();
        var transient = observation.ReasonCode is DoclingReasonCodes.EndpointUnreachable
            or DoclingReasonCodes.HealthTimeout
            or DoclingReasonCodes.Starting;
        var shouldWait = transient && now < deadline;
        DoclingCapabilityStatus status;
        if (shouldWait)
        {
            status = CreateStatus(DoclingCapabilityState.Starting,
                                  DoclingReasonCodes.Starting,
                                  observation.Detail,
                                  endpoint);
            CurrentStatus = status;
            await mDelayAsync(PollInterval, cancellationToken);
        }
        else
        {
            var reasonCode = transient ? DoclingReasonCodes.HealthTimeout : observation.ReasonCode;
            status = CreateStatus(DoclingCapabilityState.Unavailable,
                                  reasonCode,
                                  observation.Detail,
                                  endpoint);
        }

        return (!shouldWait, status);
    }

    private async Task<(bool Finished, DoclingCapabilityStatus Status)> ObserveReadinessAsync(
        DoclingFile probeFile,
        string endpoint,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var readiness = await mClient.CheckReadinessAsync(cancellationToken);
        (bool Finished, DoclingCapabilityStatus Status) result;
        if (readiness.Succeeded)
        {
            result = await ObserveConversionAsync(probeFile, endpoint, cancellationToken);
        }
        else
        {
            var now = mTimeProvider.GetUtcNow();
            var transient = readiness.ReasonCode is DoclingReasonCodes.ModelsUnavailable
                or DoclingReasonCodes.EndpointUnreachable
                or DoclingReasonCodes.HealthTimeout
                or DoclingReasonCodes.Starting;
            var shouldWait = transient && now < deadline;
            DoclingCapabilityStatus status;
            if (shouldWait)
            {
                status = CreateStatus(DoclingCapabilityState.Starting,
                                      DoclingReasonCodes.Starting,
                                      readiness.Detail,
                                      endpoint);
                CurrentStatus = status;
                await mDelayAsync(PollInterval, cancellationToken);
            }
            else
            {
                status = CreateStatus(DoclingCapabilityState.Unavailable,
                                      readiness.ReasonCode,
                                      readiness.Detail,
                                      endpoint);
            }

            result = (!shouldWait, status);
        }

        return result;
    }

    private async Task<(bool Finished, DoclingCapabilityStatus Status)> ObserveConversionAsync(
        DoclingFile probeFile,
        string endpoint,
        CancellationToken cancellationToken)
    {
        var conversion = await mClient.ConvertAsync(probeFile, cancellationToken);
        DoclingCapabilityStatus status;
        if (!conversion.Succeeded)
        {
            status = CreateStatus(DoclingCapabilityState.Unavailable,
                                  conversion.ReasonCode,
                                  conversion.Detail,
                                  endpoint);
        }
        else
        {
            if (!DoclingProbeDocument.ContainsMarker(conversion.Document))
            {
                status = CreateStatus(DoclingCapabilityState.Unavailable,
                                      DoclingReasonCodes.OutputInvalid,
                                      ProbeMarkerMissingDetail,
                                      endpoint);
            }
            else
            {
                status = CreateStatus(DoclingCapabilityState.Ready,
                                      DoclingReasonCodes.Ready,
                                      ReadyDetail,
                                      endpoint);
            }
        }

        return (true, status);
    }

    private DoclingCapabilityStatus CreateStatus(DoclingCapabilityState state,
                                                 string reasonCode,
                                                 string detail,
                                                 string endpoint) =>
        new(state,
            reasonCode,
            detail,
            endpoint,
            mTimeProvider.GetUtcNow(),
            RemediationFor(reasonCode));

    private static string RemediationFor(string reasonCode)
    {
        var result = reasonCode switch
        {
            DoclingReasonCodes.Ready => ReadyRemediation,
            DoclingReasonCodes.Starting => StartingRemediation,
            DoclingReasonCodes.NotConfigured => ConfigureRemediation,
            DoclingReasonCodes.InvalidEndpoint => EndpointRemediation,
            DoclingReasonCodes.Unauthorized => AuthenticationRemediation,
            DoclingReasonCodes.ApiIncompatible => CompatibilityRemediation,
            DoclingReasonCodes.ArtifactsUnavailable => ArtifactsRemediation,
            DoclingReasonCodes.ModelsUnavailable => ModelsRemediation,
            DoclingReasonCodes.ConversionTimeout => TimeoutRemediation,
            DoclingReasonCodes.PartialConversion or DoclingReasonCodes.ConversionFailed
                or DoclingReasonCodes.OutputInvalid => ConversionRemediation,
            _ => AvailabilityRemediation
        };
        return result;
    }

    private TimeSpan PollInterval => TimeSpan.FromMilliseconds(mSettings.StartupPollIntervalMilliseconds);

    private const string NotCheckedDetail = "Docling has not been checked.";
    private const string NotCheckedRemediation = "Run a document-ingestion status check.";
    private const string ReadyDetail = "Docling health, models, and known document conversion are ready.";
    private const string ProbeMarkerMissingDetail = "Docling converted the known probe but omitted its verification marker.";
    private const string ReadyRemediation = "No action is required.";
    private const string StartingRemediation = "Wait for the user-managed Docling process to finish initializing, then retry.";
    private const string ConfigureRemediation = "Configure the user-managed Docling Serve endpoint, then run the status test.";
    private const string EndpointRemediation = "Correct the Docling HTTP or HTTPS endpoint, then retry.";
    private const string AuthenticationRemediation = "Check the API key configured for the user-managed Docling endpoint.";
    private const string CompatibilityRemediation = "Check the installed Docling Serve v1 release and its /docs schema.";
    private const string ArtifactsRemediation = "Use the Docling documentation to make its required artifacts available.";
    private const string ModelsRemediation = "Allow the user-managed Docling process to load its models, then retry.";
    private const string TimeoutRemediation = "Increase the bounded Docling timeout or finish model initialization, then retry.";
    private const string ConversionRemediation = "Review the reported Docling conversion detail and its user-managed logs.";
    private const string AvailabilityRemediation = "Verify the user-managed Docling process and endpoint, then retry.";
}
