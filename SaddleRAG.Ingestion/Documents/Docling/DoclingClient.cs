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
        var endpoint = validation.Endpoint
                       ?? throw new InvalidOperationException(ValidatedEndpointMissingDetail);
        using var timeout = CreateTimeout(mSettings.ConversionTimeoutSeconds, cancellationToken);
        DoclingConversionResult result;
        try
        {
            result = await RunConversionAsync(endpoint,
                                              file,
                                              inputFormat,
                                              timeout.Token);
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

    private async Task<DoclingConversionResult> RunConversionAsync(Uri endpoint,
                                                                   DoclingFile file,
                                                                   string inputFormat,
                                                                   CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, AsyncConversionPath));
        request.Content = BuildMultipartContent(file, inputFormat);
        AddApiKey(request);
        var submission = await SendAsync(request, cancellationToken);
        DoclingConversionResult result;
        if (!submission.Succeeded)
        {
            result = MapConversionFailure(submission.StatusCode, submission.Body);
        }
        else
        {
            var statusResult = ReadTaskStatus(submission.Body, expectedTaskId: null);
            result = await FollowTaskAsync(endpoint, statusResult, cancellationToken);
        }

        return result;
    }

    private async Task<DoclingConversionResult> FollowTaskAsync(Uri endpoint,
                                                                DoclingTaskStatusResult statusResult,
                                                                CancellationToken cancellationToken)
    {
        DoclingConversionResult result;
        if (statusResult.Failure != null)
        {
            result = statusResult.Failure;
        }
        else
        {
            var taskStatus = statusResult.Status
                             ?? throw new InvalidOperationException(TaskStatusMissingDetail);
            while (IsInProgress(taskStatus.Status))
            {
                await Task.Delay(mSettings.ConversionPollIntervalMilliseconds, cancellationToken);
                statusResult = await PollTaskAsync(endpoint, taskStatus.TaskId, cancellationToken);
                if (statusResult.Failure != null)
                    break;
                taskStatus = statusResult.Status
                             ?? throw new InvalidOperationException(TaskStatusMissingDetail);
            }

            result = statusResult.Failure
                     ?? await CompleteTaskAsync(endpoint, taskStatus, cancellationToken);
        }

        return result;
    }

    private async Task<DoclingTaskStatusResult> PollTaskAsync(Uri endpoint,
                                                              string taskId,
                                                              CancellationToken cancellationToken)
    {
        var escapedTaskId = Uri.EscapeDataString(taskId);
        var path = $"{PollPathPrefix}{escapedTaskId}?{PollWaitParameter}={PollWaitSeconds}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, path));
        AddApiKey(request);
        var response = await SendAsync(request, cancellationToken);
        var result = response switch
        {
            { StatusCode: HttpStatusCode.NotFound } =>
                DoclingTaskStatusResult.FromStatus(new DoclingTaskStatus(taskId,
                                                                         SuccessTaskStatus,
                                                                         response.Body)),
            { Succeeded: false } =>
                DoclingTaskStatusResult.FromFailure(
                    MapConversionFailure(response.StatusCode, response.Body)
                ),
            _ => ReadTaskStatus(response.Body, taskId)
        };

        return result;
    }

    private async Task<DoclingConversionResult> CompleteTaskAsync(Uri endpoint,
                                                                  DoclingTaskStatus taskStatus,
                                                                  CancellationToken cancellationToken)
    {
        DoclingConversionResult result;
        if (taskStatus.Status is FailureTaskStatus or SkippedTaskStatus)
        {
            result = MapTaskFailure(taskStatus);
        }
        else
        {
            result = await ReadTaskResultAsync(endpoint, taskStatus.TaskId, cancellationToken);
        }

        return result;
    }

    private async Task<DoclingConversionResult> ReadTaskResultAsync(Uri endpoint,
                                                                    string taskId,
                                                                    CancellationToken cancellationToken)
    {
        var attempt = 0;
        DoclingConversionResult? result = null;
        while (result == null)
        {
            var response = await RequestTaskResultAsync(endpoint, taskId, cancellationToken);
            var retry = response.StatusCode == HttpStatusCode.NotFound
                        && attempt < ResultRetryCount;
            if (retry)
            {
                var multiplier = 1 << attempt;
                var delay = checked(mSettings.ConversionResultRetryBaseMilliseconds * multiplier);
                await Task.Delay(delay, cancellationToken);
                attempt++;
            }
            else
            {
                result = response.Succeeded
                    ? MapResultResponse(response.Body)
                    : MapConversionFailure(response.StatusCode, response.Body);
            }
        }

        return result;
    }

    private async Task<DoclingHttpResponse> RequestTaskResultAsync(Uri endpoint,
                                                                   string taskId,
                                                                   CancellationToken cancellationToken)
    {
        var escapedTaskId = Uri.EscapeDataString(taskId);
        using var request = new HttpRequestMessage(HttpMethod.Get,
                                                   new Uri(endpoint, $"{ResultPathPrefix}{escapedTaskId}"));
        AddApiKey(request);
        return await SendAsync(request, cancellationToken);
    }

    private DoclingConversionResult MapResultResponse(string body)
    {
        DoclingConversionResult result;
        if (IsTaskFailureResult(body))
        {
            result = MapTaskFailure(new DoclingTaskStatus(string.Empty,
                                                          FailureTaskStatus,
                                                          body));
        }
        else
        {
            result = mMapper.Map(body);
        }

        return result;
    }

    private static bool IsTaskFailureResult(string body)
    {
        var result = false;
        try
        {
            using var json = JsonDocument.Parse(body);
            var kind = ReadRequiredString(json.RootElement, KindProperty);
            result = string.Equals(kind, TaskFailureResultKind, StringComparison.Ordinal);
        }
        catch(JsonException)
        {
            result = false;
        }

        return result;
    }

    private async Task<DoclingHttpResponse> SendAsync(HttpRequestMessage request,
                                                       CancellationToken cancellationToken)
    {
        using var response = await mHttpClient.SendAsync(request,
                                                        HttpCompletionOption.ResponseHeadersRead,
                                                        cancellationToken);
        var body = Sanitize(await response.Content.ReadAsStringAsync(cancellationToken));
        return new DoclingHttpResponse(response.IsSuccessStatusCode, response.StatusCode, body);
    }

    private DoclingTaskStatusResult ReadTaskStatus(string body, string? expectedTaskId)
    {
        DoclingTaskStatusResult result;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var taskId = ReadRequiredString(root, TaskIdProperty);
            var taskType = ReadRequiredString(root, TaskTypeProperty);
            var taskStatus = ReadRequiredString(root, TaskStatusProperty);
            var knownTaskType = string.Equals(taskType, ConvertTaskType, StringComparison.Ordinal);
            var knownStatus = taskStatus is PendingTaskStatus
                or StartedTaskStatus
                or FailureTaskStatus
                or SuccessTaskStatus
                or PartialSuccessTaskStatus
                or SkippedTaskStatus;
            var matchesExpectedId = expectedTaskId == null
                                    || string.Equals(taskId, expectedTaskId, StringComparison.Ordinal);
            if (root.ValueKind != JsonValueKind.Object
                || string.IsNullOrEmpty(taskId)
                || string.IsNullOrEmpty(taskType)
                || string.IsNullOrEmpty(taskStatus)
                || !knownTaskType
                || !knownStatus
                || !matchesExpectedId)
            {
                result = DoclingTaskStatusResult.FromFailure(
                    DoclingConversionResult.Failure(DoclingReasonCodes.ApiIncompatible,
                                                    InvalidTaskStatusDetail,
                                                    body)
                );
            }
            else
            {
                result = DoclingTaskStatusResult.FromStatus(new DoclingTaskStatus(taskId,
                                                                                  taskStatus,
                                                                                  body));
            }
        }
        catch(JsonException ex)
        {
            result = DoclingTaskStatusResult.FromFailure(
                DoclingConversionResult.Failure(DoclingReasonCodes.ApiIncompatible,
                                                Sanitize($"{MalformedTaskStatusPrefix} {ex.Message}"),
                                                body)
            );
        }

        return result;
    }

    private DoclingConversionResult MapTaskFailure(DoclingTaskStatus taskStatus)
    {
        var fallback = taskStatus.Status == SkippedTaskStatus
            ? TaskSkippedDetail
            : TaskFailureDetail;
        var detail = ReadTaskFailureDetail(taskStatus.ResponseJson, fallback);
        var reasonCode = detail.Contains(ArtifactTerm, StringComparison.OrdinalIgnoreCase)
            ? DoclingReasonCodes.ArtifactsUnavailable
            : detail.Contains(ModelTerm, StringComparison.OrdinalIgnoreCase)
                ? DoclingReasonCodes.ModelsUnavailable
                : DoclingReasonCodes.ConversionFailed;
        return DoclingConversionResult.Failure(reasonCode, detail, taskStatus.ResponseJson);
    }

    private string ReadTaskFailureDetail(string body, string fallback)
    {
        var result = fallback;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var errorMessage = ReadRequiredString(root, ErrorMessageProperty);
            var failureMessage = ReadFailureMessage(root);
            result = !string.IsNullOrEmpty(errorMessage)
                ? errorMessage
                : !string.IsNullOrEmpty(failureMessage)
                    ? failureMessage
                    : fallback;
        }
        catch(JsonException)
        {
            result = fallback;
        }

        return NormalizeDetail(result);
    }

    private static string ReadFailureMessage(JsonElement root)
    {
        var result = string.Empty;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(FailureProperty, out var failure))
        {
            result = failure.ValueKind == JsonValueKind.String
                ? failure.GetString() ?? string.Empty
                : ReadRequiredString(failure, MessageProperty);
        }

        return result;
    }

    private static bool IsInProgress(string taskStatus) =>
        taskStatus is PendingTaskStatus or StartedTaskStatus;

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        var result = string.Empty;
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            result = property.GetString() ?? string.Empty;
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
        return NormalizeDetail(result);
    }

    private string NormalizeDetail(string detail)
    {
        var result = Sanitize(detail);
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
    private const string AsyncConversionPath = "/v1/convert/file/async";
    private const string PollPathPrefix = "/v1/status/poll/";
    private const string ResultPathPrefix = "/v1/result/";
    private const string PollWaitParameter = "wait";
    private const string ApiKeyHeader = "X-Api-Key";
    private const string StatusProperty = "status";
    private const string DetailProperty = "detail";
    private const string TaskIdProperty = "task_id";
    private const string TaskTypeProperty = "task_type";
    private const string TaskStatusProperty = "task_status";
    private const string ErrorMessageProperty = "error_message";
    private const string FailureProperty = "failure";
    private const string MessageProperty = "message";
    private const string KindProperty = "kind";
    private const string OkStatus = "ok";
    private const string ConvertTaskType = "convert";
    private const string TaskFailureResultKind = "TaskFailureResult";
    private const string PendingTaskStatus = "pending";
    private const string StartedTaskStatus = "started";
    private const string FailureTaskStatus = "failure";
    private const string SuccessTaskStatus = "success";
    private const string PartialSuccessTaskStatus = "partial_success";
    private const string SkippedTaskStatus = "skipped";
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
    private const int PollWaitSeconds = 5;
    private const int ResultRetryCount = 3;
    private const string EmptyFileDetail = "The document submitted to Docling is empty.";
    private const string UnsupportedFormatDetail = "The Docling adapter currently accepts PDF and DOCX files only.";
    private const string ValidatedEndpointMissingDetail = "Validated Docling settings did not contain an endpoint.";
    private const string EndpointFailurePrefix = "Docling endpoint request failed:";
    private const string HealthRequestTimeoutDetail = "The Docling health request timed out.";
    private const string ReadinessTimeoutDetail = "The Docling model-readiness request timed out.";
    private const string ConversionTimeoutDetail = "The Docling conversion request timed out.";
    private const string TaskStatusMissingDetail = "A validated Docling task status was unavailable.";
    private const string InvalidTaskStatusDetail = "Docling returned an invalid asynchronous task status.";
    private const string MalformedTaskStatusPrefix = "Docling returned malformed asynchronous task JSON:";
    private const string TaskFailureDetail = "Docling reported a conversion task failure.";
    private const string TaskSkippedDetail = "Docling skipped the conversion task.";
    private const string HealthOkDetail = "Docling process health is OK.";
    private const string ReadinessOkDetail = "Docling models report ready.";
    private const string HealthInvalidDetail = "Docling /health returned HTTP success without status ok.";
    private const string ReadinessInvalidDetail = "Docling /ready returned HTTP success without status ok.";
    private const string MalformedStatusPrefix = "Docling returned malformed status JSON:";
    private const string EmptyResponseDetail = "Docling returned an empty error response.";

    private sealed record DoclingTaskStatus(string TaskId, string Status, string ResponseJson);

    private sealed record DoclingTaskStatusResult(DoclingTaskStatus? Status, DoclingConversionResult? Failure)
    {
        public static DoclingTaskStatusResult FromStatus(DoclingTaskStatus status)
        {
            ArgumentNullException.ThrowIfNull(status);
            return new DoclingTaskStatusResult(status, null);
        }

        public static DoclingTaskStatusResult FromFailure(DoclingConversionResult failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            return new DoclingTaskStatusResult(null, failure);
        }
    }

    private readonly record struct DoclingHttpResponse(bool Succeeded, HttpStatusCode StatusCode, string Body);
}
