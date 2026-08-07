// DoclingClientTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
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
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK,
                                                        DoclingTestSupport.LoadFixture("docling-v1-pdf-success.json")));
        using var client = MakeClient(handler);
        var file = LoadFile("saddlerag-docling-probe.pdf", "application/pdf");

        var result = await client.ConvertAsync(file, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/convert/file", request.RequestUri.AbsolutePath);
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
    }

    [Fact]
    public async Task DocxMultipartMatchesCommittedV1Contract()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(DoclingTestSupport.JsonResponse(HttpStatusCode.OK,
                                                        DoclingTestSupport.LoadFixture("docling-v1-docx-success.json")));
        using var client = MakeClient(handler);
        var mediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        var file = LoadFile("saddlerag-docling-probe.docx", mediaType);

        var result = await client.ConvertAsync(file, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
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
                                                   DoclingTestSupport.LoadFixture("docling-v1-pdf-success.json"));
        });
        var settings = new DoclingSettings { ConversionTimeoutSeconds = 2 };
        using var client = MakeClient(handler, settings);

        var result = await client.ConvertAsync(ProbeFile(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    private static DoclingClientLease MakeClient(HttpMessageHandler handler, DoclingSettings? settings = null)
    {
        var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new DoclingClient(httpClient, settings ?? new DoclingSettings(), new DoclingDocumentMapper());
        return new DoclingClientLease(httpClient, client);
    }

    private static DoclingFile ProbeFile() =>
        new("probe.pdf", "application/pdf", new byte[] { 37, 80, 68, 70, 45, 49, 46, 55 });

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
