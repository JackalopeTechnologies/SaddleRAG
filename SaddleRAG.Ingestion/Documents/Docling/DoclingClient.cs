// DoclingClient.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

#endregion

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>
///     Typed HTTP client for the stable Docling Serve v1 boundary.
/// </summary>
public sealed class DoclingClient : IDoclingClient
{
    public DoclingClient(HttpClient httpClient, DoclingSettings settings, DoclingDocumentMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mapper);

        mHttpClient = httpClient;
        mSettings = settings;
        mMapper = mapper;
    }

    private readonly HttpClient mHttpClient;
    private readonly DoclingDocumentMapper mMapper;
    private readonly DoclingSettings mSettings;

    public Task<DoclingServiceObservation> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        CheckStatusAsync(HealthPath,
                         mSettings.HealthTimeoutSeconds,
                         readiness: false,
                         cancellationToken);

    public Task<DoclingServiceObservation> CheckReadinessAsync(CancellationToken cancellationToken = default) =>
        CheckStatusAsync(ReadinessPath,
                         mSettings.ReadinessTimeoutSeconds,
                         readiness: true,
                         cancellationToken);

    public async Task<DoclingConversionResult> ConvertAsync(DoclingFile file,
                                                            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrEmpty(file.FileName);
        ArgumentException.ThrowIfNullOrEmpty(file.MediaType);

        DoclingConversionResult result;
        var validation = mSettings.Validate();
        var extension = Path.GetExtension(file.FileName);
        var inputFormat = extension.ToLowerInvariant() switch
        {
            PdfExtension => PdfFormat,
            DocxExtension => DocxFormat,
            _ => string.Empty
        };
        if (!validation.IsValid)
        {
            result = DoclingConversionResult.Failure(validation.ReasonCode, validation.Detail);
        }
        else
        {
            if (file.Content.IsEmpty)
            {
                result = DoclingConversionResult.Failure(DoclingReasonCodes.OutputInvalid, EmptyFileDetail);
            }
            else
            {
                if (string.IsNullOrEmpty(inputFormat))
                {
                    result = DoclingConversionResult.Failure(DoclingReasonCodes.ApiIncompatible,
                                                             UnsupportedFormatDetail);
                }
                else
                {
                    result = await SendConversionAsync(validation,
                                                       file,
                                                       inputFormat,
                                                       cancellationToken);
                }
            }
        }

        return result;
    }

    private async Task<DoclingServiceObservation> CheckStatusAsync(string path,
                                                                  int timeoutSeconds,
                                                                  bool readiness,
                                                                  CancellationToken cancellationToken)
    {
        DoclingServiceObservation result;
        var validation = mSettings.Validate();
        if (!validation.IsValid)
        {
            result = DoclingServiceObservation.Failure(validation.ReasonCode, validation.Detail);
        }
        else
        {
            var endpoint = validation.Endpoint
                           ?? throw new InvalidOperationException(ValidatedEndpointMissingDetail);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, path));
            AddApiKey(request);
            using var timeout = CreateTimeout(timeoutSeconds, cancellationToken);
            try
            {
                using var response = await mHttpClient.SendAsync(request,
                                                                HttpCompletionOption.ResponseHeadersRead,
                                                                timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                result = MapStatusResponse(response.StatusCode, body, readiness);
            }
            catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch(OperationCanceledException)
            {
                var reasonCode = readiness ? DoclingReasonCodes.ModelsUnavailable : DoclingReasonCodes.HealthTimeout;
                var detail = readiness ? ReadinessTimeoutDetail : HealthRequestTimeoutDetail;
                result = DoclingServiceObservation.Failure(reasonCode, detail);
            }
            catch(HttpRequestException ex)
            {
                result = DoclingServiceObservation.Failure(DoclingReasonCodes.EndpointUnreachable,
                                                           Sanitize($"{EndpointFailurePrefix} {ex.Message}"));
            }
        }

        return result;
    }

    private async Task<DoclingConversionResult> SendConversionAsync(DoclingSettingsValidation validation,
                                                                    DoclingFile file,
                                                                    string inputFormat,
                                                                    CancellationToken cancellationToken)
    {
        DoclingConversionResult result;
        var endpoint = validation.Endpoint
                       ?? throw new InvalidOperationException(ValidatedEndpointMissingDetail);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, ConversionPath));
        request.Content = BuildMultipartContent(file, inputFormat);
        AddApiKey(request);
        using var timeout = CreateTimeout(mSettings.ConversionTimeoutSeconds, cancellationToken);
        try
        {
            using var response = await mHttpClient.SendAsync(request,
                                                            HttpCompletionOption.ResponseHeadersRead,
                                                            timeout.Token);
            var body = Sanitize(await response.Content.ReadAsStringAsync(timeout.Token));
            result = response.IsSuccessStatusCode
                ? mMapper.Map(body)
                : MapConversionFailure(response.StatusCode, body);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch(OperationCanceledException)
        {
            result = DoclingConversionResult.Failure(DoclingReasonCodes.ConversionTimeout,
                                                     ConversionTimeoutDetail);
        }
        catch(HttpRequestException ex)
        {
            result = DoclingConversionResult.Failure(DoclingReasonCodes.EndpointUnreachable,
                                                     Sanitize($"{EndpointFailurePrefix} {ex.Message}"));
        }

        return result;
    }

    private static MultipartFormDataContent BuildMultipartContent(DoclingFile file, string inputFormat)
    {
        var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(file.Content.ToArray());
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(file.MediaType);
        multipart.Add(fileContent, FilesField, file.FileName);
        AddFormField(multipart, FromFormatsField, inputFormat);
        AddFormField(multipart, ToFormatsField, MarkdownFormat);
        AddFormField(multipart, ToFormatsField, JsonFormat);
        AddFormField(multipart, ToFormatsField, TextFormat);
        AddFormField(multipart, DoOcrField, TrueValue);
        AddFormField(multipart, TableModeField, AccurateValue);
        AddFormField(multipart, PipelineField, StandardValue);
        AddFormField(multipart, ImageExportModeField, PlaceholderValue);
        AddFormField(multipart, PictureDescriptionField, FalseValue);
        AddFormField(multipart, PictureClassificationField, FalseValue);
        AddFormField(multipart, CodeEnrichmentField, FalseValue);
        AddFormField(multipart, FormulaEnrichmentField, FalseValue);
        AddFormField(multipart, AbortOnErrorField, FalseValue);
        return multipart;
    }

    private static void AddFormField(MultipartFormDataContent content, string name, string value)
    {
        content.Add(new StringContent(value, Encoding.UTF8), name);
    }

    private static CancellationTokenSource CreateTimeout(int timeoutSeconds, CancellationToken cancellationToken)
    {
        var result = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        result.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        return result;
    }

    private void AddApiKey(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(mSettings.ApiKey))
            request.Headers.Add(ApiKeyHeader, mSettings.ApiKey.Trim());
    }

    private DoclingServiceObservation MapStatusResponse(HttpStatusCode statusCode,
                                                        string body,
                                                        bool readiness)
    {
        var safeDetail = ReadResponseDetail(body);
        var result = statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                DoclingServiceObservation.Failure(DoclingReasonCodes.Unauthorized, safeDetail),
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed =>
                DoclingServiceObservation.Failure(DoclingReasonCodes.ApiIncompatible, safeDetail),
            HttpStatusCode.ServiceUnavailable when readiness =>
                DoclingServiceObservation.Failure(DoclingReasonCodes.ModelsUnavailable, safeDetail),
            >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices => ReadOkStatus(body, readiness),
            _ => DoclingServiceObservation.Failure(
                readiness ? DoclingReasonCodes.ModelsUnavailable : DoclingReasonCodes.HealthInvalid,
                safeDetail)
        };

        return result;
    }

    private static DoclingServiceObservation ReadOkStatus(string body, bool readiness)
    {
        DoclingServiceObservation result;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var valid = root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty(StatusProperty, out var status)
                        && status.ValueKind == JsonValueKind.String
                        && string.Equals(status.GetString(), OkStatus, StringComparison.OrdinalIgnoreCase);
            var detail = readiness ? ReadinessOkDetail : HealthOkDetail;
            var invalidDetail = readiness ? ReadinessInvalidDetail : HealthInvalidDetail;
            var invalidCode = readiness ? DoclingReasonCodes.ApiIncompatible : DoclingReasonCodes.HealthInvalid;
            result = valid
                ? DoclingServiceObservation.Success(detail)
                : DoclingServiceObservation.Failure(invalidCode, invalidDetail);
        }
        catch(JsonException ex)
        {
            var reasonCode = readiness ? DoclingReasonCodes.ApiIncompatible : DoclingReasonCodes.HealthInvalid;
            result = DoclingServiceObservation.Failure(reasonCode,
                                                       $"{MalformedStatusPrefix} {ex.Message}");
        }

        return result;
    }

    private DoclingConversionResult MapConversionFailure(HttpStatusCode statusCode, string body)
    {
        var detail = ReadResponseDetail(body);
        var reasonCode = statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DoclingReasonCodes.Unauthorized,
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.UnprocessableEntity =>
                DoclingReasonCodes.ApiIncompatible,
            HttpStatusCode.ServiceUnavailable when detail.Contains(ArtifactTerm,
                                                                   StringComparison.OrdinalIgnoreCase) =>
                DoclingReasonCodes.ArtifactsUnavailable,
            HttpStatusCode.ServiceUnavailable when detail.Contains(ModelTerm,
                                                                   StringComparison.OrdinalIgnoreCase) =>
                DoclingReasonCodes.ModelsUnavailable,
            _ => DoclingReasonCodes.ConversionFailed
        };
        return DoclingConversionResult.Failure(reasonCode, detail, body);
    }

    private string ReadResponseDetail(string body)
    {
        var result = body;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(DetailProperty, out var detail)
                && detail.ValueKind == JsonValueKind.String)
            {
                result = detail.GetString() ?? body;
            }
        }
        catch(JsonException)
        {
            result = body;
        }

        if (string.IsNullOrWhiteSpace(result))
            result = EmptyResponseDetail;
        result = Sanitize(result);
        if (result.Length > DetailLengthLimit)
            result = result[..DetailLengthLimit] + TruncatedSuffix;
        return result;
    }

    private string Sanitize(string value)
    {
        var result = value;
        if (!string.IsNullOrWhiteSpace(mSettings.ApiKey))
            result = result.Replace(mSettings.ApiKey.Trim(), RedactedValue, StringComparison.Ordinal);
        return result;
    }

    private const string HealthPath = "/health";
    private const string ReadinessPath = "/ready";
    private const string ConversionPath = "/v1/convert/file";
    private const string ApiKeyHeader = "X-Api-Key";
    private const string StatusProperty = "status";
    private const string DetailProperty = "detail";
    private const string OkStatus = "ok";
    private const string PdfExtension = ".pdf";
    private const string DocxExtension = ".docx";
    private const string PdfFormat = "pdf";
    private const string DocxFormat = "docx";
    private const string MarkdownFormat = "md";
    private const string JsonFormat = "json";
    private const string TextFormat = "text";
    private const string FilesField = "files";
    private const string FromFormatsField = "from_formats";
    private const string ToFormatsField = "to_formats";
    private const string DoOcrField = "do_ocr";
    private const string TableModeField = "table_mode";
    private const string PipelineField = "pipeline";
    private const string ImageExportModeField = "image_export_mode";
    private const string PictureDescriptionField = "do_picture_description";
    private const string PictureClassificationField = "do_picture_classification";
    private const string CodeEnrichmentField = "do_code_enrichment";
    private const string FormulaEnrichmentField = "do_formula_enrichment";
    private const string AbortOnErrorField = "abort_on_error";
    private const string TrueValue = "true";
    private const string FalseValue = "false";
    private const string AccurateValue = "accurate";
    private const string StandardValue = "standard";
    private const string PlaceholderValue = "placeholder";
    private const string ArtifactTerm = "artifact";
    private const string ModelTerm = "model";
    private const string RedactedValue = "[REDACTED]";
    private const string TruncatedSuffix = "...";
    private const int DetailLengthLimit = 1024;
    private const string EmptyFileDetail = "The document submitted to Docling is empty.";
    private const string UnsupportedFormatDetail = "The Docling adapter currently accepts PDF and DOCX files only.";
    private const string ValidatedEndpointMissingDetail = "Validated Docling settings did not contain an endpoint.";
    private const string EndpointFailurePrefix = "Docling endpoint request failed:";
    private const string HealthRequestTimeoutDetail = "The Docling health request timed out.";
    private const string ReadinessTimeoutDetail = "The Docling model-readiness request timed out.";
    private const string ConversionTimeoutDetail = "The Docling conversion request timed out.";
    private const string HealthOkDetail = "Docling process health is OK.";
    private const string ReadinessOkDetail = "Docling models report ready.";
    private const string HealthInvalidDetail = "Docling /health returned HTTP success without status ok.";
    private const string ReadinessInvalidDetail = "Docling /ready returned HTTP success without status ok.";
    private const string MalformedStatusPrefix = "Docling returned malformed status JSON:";
    private const string EmptyResponseDetail = "Docling returned an empty error response.";
}
