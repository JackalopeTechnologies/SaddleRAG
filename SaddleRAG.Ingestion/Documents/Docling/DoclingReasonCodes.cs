// DoclingReasonCodes.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>
///     Stable machine-readable outcomes for the optional Docling capability.
/// </summary>
public static class DoclingReasonCodes
{
    public const string Ready = "DOCLING_READY";
    public const string NotConfigured = "DOCLING_NOT_CONFIGURED";
    public const string InvalidEndpoint = "DOCLING_INVALID_ENDPOINT";
    public const string Starting = "DOCLING_STARTING";
    public const string EndpointUnreachable = "DOCLING_ENDPOINT_UNREACHABLE";
    public const string HealthTimeout = "DOCLING_HEALTH_TIMEOUT";
    public const string Unauthorized = "DOCLING_UNAUTHORIZED";
    public const string HealthInvalid = "DOCLING_HEALTH_INVALID";
    public const string ApiIncompatible = "DOCLING_API_INCOMPATIBLE";
    public const string ArtifactsUnavailable = "DOCLING_ARTIFACTS_UNAVAILABLE";
    public const string ModelsUnavailable = "DOCLING_MODELS_UNAVAILABLE";
    public const string ConversionTimeout = "DOCLING_CONVERSION_TIMEOUT";
    public const string ConversionStalled = "DOCLING_CONVERSION_STALLED";
    public const string PartialConversion = "DOCLING_PARTIAL_CONVERSION";
    public const string ConversionFailed = "DOCLING_CONVERSION_FAILED";
    public const string OutputInvalid = "DOCLING_OUTPUT_INVALID";
    public const string ProbeFailed = "DOCLING_PROBE_FAILED";
}
