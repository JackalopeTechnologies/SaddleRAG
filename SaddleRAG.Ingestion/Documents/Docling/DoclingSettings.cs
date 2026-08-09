// DoclingSettings.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>
///     Configuration for the user-operated Docling Serve endpoint.
/// </summary>
public sealed class DoclingSettings
{
    /// <summary>Configured Docling Serve root URL.</summary>
    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>Optional Docling Serve API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Cold-start grace period before a transient startup state becomes unavailable.</summary>
    public int StartupGracePeriodSeconds { get; set; } = DefaultStartupGracePeriodSeconds;

    /// <summary>Timeout for one liveness request.</summary>
    public int HealthTimeoutSeconds { get; set; } = DefaultHealthTimeoutSeconds;

    /// <summary>Timeout for one model-readiness request.</summary>
    public int ReadinessTimeoutSeconds { get; set; } = DefaultReadinessTimeoutSeconds;

    /// <summary>Backstop for one conversion, including cold model initialization.</summary>
    public int ConversionTimeoutSeconds { get; set; } = DefaultConversionTimeoutSeconds;

    /// <summary>Maximum uninterrupted run of failed status polls before a conversion is abandoned.</summary>
    public int ConversionStallTimeoutSeconds { get; set; } = DefaultConversionStallTimeoutSeconds;

    /// <summary>Delay between bounded cold-start observations.</summary>
    public int StartupPollIntervalMilliseconds { get; set; } = DefaultStartupPollIntervalMilliseconds;

    /// <summary>Delay between asynchronous conversion-status observations.</summary>
    public int ConversionPollIntervalMilliseconds { get; set; } = DefaultConversionPollIntervalMilliseconds;

    /// <summary>Base delay for bounded retries while a completed task result becomes available.</summary>
    public int ConversionResultRetryBaseMilliseconds { get; set; } = DefaultConversionResultRetryBaseMilliseconds;

    /// <summary>Validates configuration without contacting or controlling Docling.</summary>
    public DoclingSettingsValidation Validate()
    {
        DoclingSettingsValidation result;
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            result = new DoclingSettingsValidation(false,
                                                   DoclingReasonCodes.NotConfigured,
                                                   NotConfiguredDetail,
                                                   null);
        }
        else
        {
            var parsed = Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var endpoint);
            var supportedScheme = parsed
                                  && endpoint != null
                                  && (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps);
            var credentialsAreSeparate = supportedScheme
                                         && endpoint != null
                                         && string.IsNullOrEmpty(endpoint.UserInfo)
                                         && string.IsNullOrEmpty(endpoint.Query)
                                         && string.IsNullOrEmpty(endpoint.Fragment);
            var timeoutsValid = StartupGracePeriodSeconds > 0
                                && HealthTimeoutSeconds > 0
                                && ReadinessTimeoutSeconds > 0
                                && ConversionTimeoutSeconds > 0
                                && ConversionStallTimeoutSeconds > 0
                                && StartupPollIntervalMilliseconds > 0
                                && ConversionPollIntervalMilliseconds is > 0 and <= MaximumPollIntervalMilliseconds
                                && ConversionResultRetryBaseMilliseconds is > 0 and <= MaximumPollIntervalMilliseconds;
            var valid = credentialsAreSeparate && timeoutsValid;
            var detail = valid
                ? ValidDetail
                : timeoutsValid
                    ? InvalidEndpointDetail
                    : InvalidTimeoutDetail;
            result = new DoclingSettingsValidation(valid,
                                                   valid ? DoclingReasonCodes.Ready : DoclingReasonCodes.InvalidEndpoint,
                                                   detail,
                                                   valid ? endpoint : null);
        }

        return result;
    }

    public const string SectionName = "DocumentIngestion:Docling";
    public const string DefaultEndpoint = "http://localhost:5001";
    public const int DefaultStartupGracePeriodSeconds = 120;
    public const int DefaultHealthTimeoutSeconds = 10;
    public const int DefaultReadinessTimeoutSeconds = 30;
    public const int DefaultConversionTimeoutSeconds = 14400;
    public const int DefaultConversionStallTimeoutSeconds = 300;
    public const int DefaultStartupPollIntervalMilliseconds = 1000;
    public const int DefaultConversionPollIntervalMilliseconds = 5000;
    public const int DefaultConversionResultRetryBaseMilliseconds = 2000;
    public const int MaximumPollIntervalMilliseconds = 60000;

    private const string NotConfiguredDetail = "No Docling endpoint is configured.";
    private const string InvalidEndpointDetail =
        "The Docling endpoint must be an absolute HTTP or HTTPS URL without embedded credentials, query, or fragment.";
    private const string InvalidTimeoutDetail =
        "Docling grace, request, and stall timeouts must be greater than zero; poll intervals must be between 1 and 60000 milliseconds.";
    private const string ValidDetail = "Docling configuration is valid.";
}
