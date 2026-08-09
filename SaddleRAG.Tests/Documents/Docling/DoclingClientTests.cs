// DoclingClientTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using System.Text.Json;
using SaddleRAG.Ingestion.Documents.Docling;

#endregion

namespace SaddleRAG.Tests.Documents.Docling;

public sealed class DoclingClientTests
{
    [Fact]
    public async Task HealthUsesLivenessEndpointAndAcceptsOkPayload()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}"));
        using var client = MakeClient(handler);

        var result = await client.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("/health", Assert.Single(handler.Requests).RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task ReadinessKeepsMissingModelsDistinct()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.ServiceUnavailable,
                                                        "{\"detail\":\"Models not yet loaded\"}"));
        using var client = MakeClient(handler);

        var result = await client.CheckReadinessAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.ModelsUnavailable, result.ReasonCode);
        Assert.Contains("Models not yet loaded", result.Detail, StringComparison.Ordinal);
        Assert.Equal("/ready", Assert.Single(handler.Requests).RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task MalformedHealthPayloadIsDistinct()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, "{not valid json"));
        using var client = MakeClient(handler);

        var result = await client.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.HealthInvalid, result.ReasonCode);
    }

    [Fact]
    public async Task PdfMultipartMatchesCommittedV1Contract()
    {
        var handler = new ScriptedHttpMessageHandler();
        EnqueueSuccessfulConversion(handler, "docling-v1-pdf-success.json");
        using var client = MakeClient(handler);
        var file = LoadFile("saddlerag-docling-probe.pdf", "application/pdf");

        var result = await client.ConvertAsync(file, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(expected: 2, handler.Requests.Count);
        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/convert/file/async", request.RequestUri.AbsolutePath);
        Assert.StartsWith("multipart/form-data", request.ContentType, StringComparison.OrdinalIgnoreCase);
        AssertMultipartField(request.Body, "files", "saddlerag-docling-probe.pdf");
        AssertMultipartField(request.Body, "from_formats", "pdf");
        AssertMultipartField(request.Body, "to_formats", "md");
        AssertMultipartField(request.Body, "to_formats", "json");
        AssertMultipartField(request.Body, "to_formats", "text");
        AssertMultipartField(request.Body, "do_ocr", "true");
        AssertMultipartField(request.Body, "table_mode", "accurate");
        AssertMultipartField(request.Body, "pipeline", "standard");
        AssertMultipartField(request.Body, "do_picture_description", "false");
        Assert.Equal(expected: 1, CountOccurrences(request.Body, "name=files"));
        Assert.Equal(expected: 3, CountOccurrences(request.Body, "name=to_formats"));
        Assert.Equal($"/v1/result/{TaskId}", handler.Requests[1].RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task DocxMultipartMatchesCommittedV1Contract()
    {
        var handler = new ScriptedHttpMessageHandler();
        EnqueueSuccessfulConversion(handler, "docling-v1-docx-success.json");
        using var client = MakeClient(handler);
        var mediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        var file = LoadFile("saddlerag-docling-probe.docx", mediaType);

        var result = await client.ConvertAsync(file, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(expected: 2, handler.Requests.Count);
        var request = handler.Requests[0];
        AssertMultipartField(request.Body, "files", "saddlerag-docling-probe.docx");
        AssertMultipartField(request.Body, "from_formats", "docx");
        Assert.Contains(mediaType, request.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApiKeyHeaderIsSentOnlyWhenConfigured()
    {
        var keyedHandler = new ScriptedHttpMessageHandler();
        keyedHandler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}"));
        var keyedSettings = new DoclingSettings { ApiKey = "private-test-key" };
        using var keyedClient = MakeClient(keyedHandler, keyedSettings);

        await keyedClient.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal("private-test-key", Assert.Single(keyedHandler.Requests).ApiKey);

        var anonymousHandler = new ScriptedHttpMessageHandler();
        anonymousHandler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}"));
        using var anonymousClient = MakeClient(anonymousHandler);

        await anonymousClient.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.Empty(Assert.Single(anonymousHandler.Requests).ApiKey);
    }

    [Fact]
    public async Task UnauthorizedResponseIsDistinctAndApiKeyIsRedacted()
    {
        const string secret = "private-test-key";
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                        {
                            Content = new StringContent($"API key {secret} was rejected")
                        });
        using var client = MakeClient(handler, new DoclingSettings { ApiKey = secret });

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.Unauthorized, result.ReasonCode);
        Assert.DoesNotContain(secret, result.Detail, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionRefusalIsDistinct()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue((_, _) => throw new HttpRequestException("Connection refused"));
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.EndpointUnreachable, result.ReasonCode);
        Assert.Contains("Connection refused", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConversionTimeoutIsDistinct()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue((_, _) => throw new TaskCanceledException("simulated timeout"));
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.ConversionTimeout, result.ReasonCode);
    }

    [Fact]
    public async Task ExternalCancellationIsRethrown()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return DoclingTestSupport.JsonResponse(HttpStatusCode.OK, "{}");
        });
        using var client = MakeClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ConvertAsync(ProbeFile(), cancellation.Token)
        );
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task IncompatibleV1ApiIsDistinct(HttpStatusCode statusCode)
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(statusCode, "{\"detail\":\"route or schema mismatch\"}"));
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.ApiIncompatible, result.ReasonCode);
    }

    [Theory]
    [InlineData("model weights are missing", "DOCLING_MODELS_UNAVAILABLE")]
    [InlineData("artifacts path is unavailable", "DOCLING_ARTIFACTS_UNAVAILABLE")]
    public async Task ServerPrerequisiteFailuresRemainDistinct(string detail, string expectedCode)
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.ServiceUnavailable,
                                                        $"{{\"detail\":\"{detail}\"}}"));
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.ReasonCode);
    }

    [Fact]
    public async Task UsefulConversionFailureDetailIsRetained()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.InternalServerError,
                                                        "{\"detail\":\"PDF backend could not open the document\"}"));
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.ConversionFailed, result.ReasonCode);
        Assert.Contains("PDF backend", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowColdConversionSucceedsWithinConfiguredTimeout()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            return DoclingTestSupport.JsonResponse(HttpStatusCode.OK,
                                                   TaskStatus(SuccessTaskStatus));
        });
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK,
                                                        DoclingTestSupport.LoadFixture("docling-v1-pdf-success.json")));
        var settings = new DoclingSettings { ConversionTimeoutSeconds = 2 };
        using var client = MakeClient(handler, settings);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task PendingTaskIsPolledThroughStartedAndSuccessBeforeReadingResult()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, TaskStatus(PendingTaskStatus)));
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, TaskStatus(StartedTaskStatus)));
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, TaskStatus(SuccessTaskStatus)));
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK,
                                                        DoclingTestSupport.LoadFixture("docling-v1-pdf-success.json")));
        const string apiKey = "private-test-key";
        using var client = MakeClient(handler, FastSettings(apiKey));

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(expected: 4, handler.Requests.Count);
        Assert.Equal("/v1/convert/file/async", handler.Requests[0].RequestUri.AbsolutePath);
        Assert.Equal($"/v1/status/poll/{TaskId}", handler.Requests[1].RequestUri.AbsolutePath);
        Assert.Equal("?wait=5", handler.Requests[1].RequestUri.Query);
        Assert.Equal($"/v1/status/poll/{TaskId}", handler.Requests[2].RequestUri.AbsolutePath);
        Assert.Equal("?wait=5", handler.Requests[2].RequestUri.Query);
        Assert.Equal($"/v1/result/{TaskId}", handler.Requests[3].RequestUri.AbsolutePath);
        Assert.All(handler.Requests, request => Assert.Equal(apiKey, request.ApiKey));
    }

    [Fact]
    public async Task ImmediateSuccessReadsResultWithoutPolling()
    {
        var handler = new ScriptedHttpMessageHandler();
        EnqueueSuccessfulConversion(handler, "docling-v1-pdf-success.json");
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(expected: 2, handler.Requests.Count);
        Assert.Equal($"/v1/result/{TaskId}", handler.Requests[1].RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task PartialSuccessReadsResultAndPreservesPartialOutcome()
    {
        var handler = new ScriptedHttpMessageHandler();
        var partialResult = DoclingTestSupport.LoadFixture("docling-v1-pdf-success.json")
                                              .Replace("\"status\": \"success\"",
                                                       "\"status\": \"partial_success\"",
                                                       StringComparison.Ordinal);
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK,
                                                        TaskStatus(PartialSuccessTaskStatus)));
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, partialResult));
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.PartialConversion, result.ReasonCode);
        Assert.Equal(expected: 2, handler.Requests.Count);
        Assert.Equal($"/v1/result/{TaskId}", handler.Requests[1].RequestUri.AbsolutePath);
    }

    [Theory]
    [InlineData("{ definitely not json")]
    [InlineData("{\"task_type\":\"convert\",\"task_status\":\"pending\"}")]
    [InlineData("{\"task_id\":42,\"task_type\":\"convert\",\"task_status\":\"pending\"}")]
    [InlineData("{\"task_id\":\"task-123\",\"task_status\":\"pending\"}")]
    [InlineData("{\"task_id\":\"task-123\",\"task_type\":\"other\",\"task_status\":\"pending\"}")]
    [InlineData("{\"task_id\":\"task-123\",\"task_type\":\"convert\"}")]
    [InlineData("{\"task_id\":\"task-123\",\"task_type\":\"convert\",\"task_status\":\"unknown\"}")]
    public async Task MalformedTaskStatusIsRejected(string responseJson)
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, responseJson));
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.ApiIncompatible, result.ReasonCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PollTaskIdMustMatchSubmittedTaskId()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, TaskStatus(PendingTaskStatus)));
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK,
                                                        TaskStatus(SuccessTaskStatus, "different-task")));
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.ApiIncompatible, result.ReasonCode);
        Assert.Equal(expected: 2, handler.Requests.Count);
    }

    [Fact]
    public async Task TaskIdIsEscapedAsOneUrlPathSegment()
    {
        const string unsafeTaskId = "task/path?query#fragment";
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK,
                                                        TaskStatus(PendingTaskStatus, unsafeTaskId)));
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK,
                                                        TaskStatus(SuccessTaskStatus, unsafeTaskId)));
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK,
                                                        DoclingTestSupport.LoadFixture("docling-v1-pdf-success.json")));
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("/v1/status/poll/task%2Fpath%3Fquery%23fragment", handler.Requests[1].RequestUri.AbsolutePath);
        Assert.Equal("/v1/result/task%2Fpath%3Fquery%23fragment", handler.Requests[2].RequestUri.AbsolutePath);
    }

    [Theory]
    [InlineData("error_message")]
    [InlineData("failure")]
    public async Task FailedTaskReturnsSanitizedServerDetail(string detailShape)
    {
        const string secret = "private-test-key";
        var failure = detailShape == "error_message"
            ? $"{{\"task_id\":\"{TaskId}\",\"task_type\":\"convert\",\"task_status\":\"failure\",\"error_message\":\"backend rejected {secret}\"}}"
            : $"{{\"task_id\":\"{TaskId}\",\"task_type\":\"convert\",\"task_status\":\"failure\",\"failure\":{{\"message\":\"backend rejected {secret}\"}}}}";
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, failure));
        using var client = MakeClient(handler, new DoclingSettings { ApiKey = secret });

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.ConversionFailed, result.ReasonCode);
        Assert.Contains("backend rejected [REDACTED]", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.RawResponseJson, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SkippedTaskReturnsConversionFailureWithoutResultRequest()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, TaskStatus(SkippedTaskStatus)));
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.ConversionFailed, result.ReasonCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task HttpOkTaskFailureResultIsMappedAndSanitized()
    {
        const string secret = "private-test-key";
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, TaskStatus(SuccessTaskStatus)));
        handler.Enqueue(DoclingTestSupport.JsonResponse(
                            HttpStatusCode.OK,
                            $"{{\"kind\":\"TaskFailureResult\",\"failure\":{{\"message\":\"model weights missing {secret}\"}}}}"
                        ));
        using var client = MakeClient(handler, FastSettings(secret));

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.ModelsUnavailable, result.ReasonCode);
        Assert.Contains("model weights missing [REDACTED]", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.RawResponseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletedTaskRetriesEventuallyAvailableResult()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, TaskStatus(SuccessTaskStatus)));
        for (var index = 0; index < 3; index++)
            handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.NotFound, "{\"detail\":\"not ready\"}"));
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK,
                                                        DoclingTestSupport.LoadFixture("docling-v1-pdf-success.json")));
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(expected: 5, handler.Requests.Count);
        Assert.All(handler.Requests.Skip(1),
                   request => Assert.Equal($"/v1/result/{TaskId}", request.RequestUri.AbsolutePath));
    }

    [Fact]
    public async Task MissingPollStatusFallsThroughToEventuallyAvailableResult()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, TaskStatus(PendingTaskStatus)));
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.NotFound, "{\"detail\":\"task moved\"}"));
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK,
                                                        DoclingTestSupport.LoadFixture("docling-v1-pdf-success.json")));
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(expected: 3, handler.Requests.Count);
        Assert.Equal($"/v1/result/{TaskId}", handler.Requests[2].RequestUri.AbsolutePath);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "DOCLING_UNAUTHORIZED")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "DOCLING_API_INCOMPATIBLE")]
    public async Task PollHttpFailuresAreMapped(HttpStatusCode statusCode, string expectedReasonCode)
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, TaskStatus(PendingTaskStatus)));
        handler.Enqueue(DoclingTestSupport.JsonResponse(statusCode, "{\"detail\":\"poll failed\"}"));
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedReasonCode, result.ReasonCode);
        Assert.Equal(expected: 2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "DOCLING_UNAUTHORIZED")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "DOCLING_API_INCOMPATIBLE")]
    public async Task ResultHttpFailuresAreMapped(HttpStatusCode statusCode, string expectedReasonCode)
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, TaskStatus(SuccessTaskStatus)));
        handler.Enqueue(DoclingTestSupport.JsonResponse(statusCode, "{\"detail\":\"result failed\"}"));
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedReasonCode, result.ReasonCode);
        Assert.Equal(expected: 2, handler.Requests.Count);
    }

    [Fact]
    public async Task ConversionTimeoutDuringPollingIsDistinct()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, TaskStatus(PendingTaskStatus)));
        handler.Enqueue((_, _) => throw new TaskCanceledException("simulated poll timeout"));
        using var client = MakeClient(handler);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DoclingReasonCodes.ConversionTimeout, result.ReasonCode);
        Assert.Equal(expected: 2, handler.Requests.Count);
    }

    [Fact]
    public async Task ExternalCancellationDuringPollingIsRethrown()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, TaskStatus(PendingTaskStatus)));
        handler.Enqueue(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return DoclingTestSupport.JsonResponse(HttpStatusCode.OK, TaskStatus(SuccessTaskStatus));
        });
        using var client = MakeClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ConvertAsync(ProbeFile(), cancellation.Token)
        );
        Assert.Equal(expected: 2, handler.Requests.Count);
    }

    private static DoclingClientLease MakeClient(HttpMessageHandler handler, DoclingSettings? settings = null)
    {
        var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new DoclingClient(httpClient, settings ?? FastSettings(), new DoclingDocumentMapper());
        return new DoclingClientLease(httpClient, client);
    }

    private static DoclingSettings FastSettings(string apiKey = "") =>
        new()
        {
            ApiKey = apiKey,
            ConversionPollIntervalMilliseconds = 1,
            ConversionResultRetryBaseMilliseconds = 1
        };

    private static DoclingFile ProbeFile() =>
        new("probe.pdf", "application/pdf", new byte[] { 37, 80, 68, 70, 45, 49, 46, 55 });

    private static void EnqueueSuccessfulConversion(ScriptedHttpMessageHandler handler, string fixtureName)
    {
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK, TaskStatus(SuccessTaskStatus)));
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK,
                                                        DoclingTestSupport.LoadFixture(fixtureName)));
    }

    private static string TaskStatus(string status, string taskId = TaskId) =>
        JsonSerializer.Serialize(new
                                 {
                                     task_id = taskId,
                                     task_type = "convert",
                                     task_status = status
                                 });

    private static DoclingFile LoadFile(string fileName, string mediaType)
    {
        var path = Path.Combine(DoclingTestSupport.RepositoryRoot(),
                                "SaddleRAG.Tests",
                                "TestData",
                                "Documents",
                                fileName);
        return new DoclingFile(fileName, mediaType, File.ReadAllBytes(path));
    }

    private static void AssertMultipartField(string body, string fieldName, string value)
    {
        Assert.Contains($"name={fieldName}", body, StringComparison.Ordinal);
        Assert.Contains(value, body, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while (offset < value.Length)
        {
            var match = value.IndexOf(search, offset, StringComparison.Ordinal);
            if (match < 0)
                offset = value.Length;
            else
            {
                count++;
                offset = match + search.Length;
            }
        }

        return count;
    }

    private const string TaskId = "task-123";
    private const string PendingTaskStatus = "pending";
    private const string StartedTaskStatus = "started";
    private const string SuccessTaskStatus = "success";
    private const string PartialSuccessTaskStatus = "partial_success";
    private const string SkippedTaskStatus = "skipped";

    private sealed class DoclingClientLease : IDoclingClient, IDisposable
    {
        public DoclingClientLease(HttpClient httpClient, DoclingClient client)
        {
            ArgumentNullException.ThrowIfNull(httpClient);
            ArgumentNullException.ThrowIfNull(client);
            mHttpClient = httpClient;
            mClient = client;
        }

        private readonly DoclingClient mClient;
        private readonly HttpClient mHttpClient;

        public Task<DoclingServiceObservation> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            mClient.CheckHealthAsync(cancellationToken);

        public Task<DoclingServiceObservation> CheckReadinessAsync(CancellationToken cancellationToken = default) =>
            mClient.CheckReadinessAsync(cancellationToken);

        public Task<DoclingConversionResult> ConvertAsync(DoclingFile file,
                                                          CancellationToken cancellationToken = default) =>
            mClient.ConvertAsync(file, cancellationToken);

        public void Dispose()
        {
            mHttpClient.Dispose();
        }
    }
}
