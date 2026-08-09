// DoclingTestSupport.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using System.Text;
using SaddleRAG.Ingestion.Documents.Docling;

#endregion

namespace SaddleRAG.Tests.Documents.Docling;

internal static class DoclingTestSupport
{
    public static string LoadFixture(string fileName)
    {
        var projectDirectory = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..")
        );
        var path = Path.Combine(projectDirectory, "TestData", "Documents", fileName);
        return File.ReadAllText(path);
    }

    public static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    public static string RepositoryRoot()
    {
        var projectDirectory = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..")
        );
        return Directory.GetParent(projectDirectory)?.FullName
               ?? throw new InvalidOperationException("Unable to locate repository root.");
    }
}

internal sealed record RecordedHttpRequest(HttpMethod Method,
                                           Uri RequestUri,
                                           string Body,
                                           string ContentType,
                                           string ApiKey);

internal sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> mResponses = new();

    public List<RecordedHttpRequest> Requests { get; } = [];

    public void Enqueue(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        mResponses.Enqueue((_, _) => Task.FromResult(response));
    }

    public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
    {
        ArgumentNullException.ThrowIfNull(response);
        mResponses.Enqueue(response);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                 CancellationToken cancellationToken)
    {
        var content = request.Content;
        var body = content == null ? string.Empty : await content.ReadAsStringAsync(cancellationToken);
        var contentType = content?.Headers.ContentType?.ToString() ?? string.Empty;
        var apiKey = request.Headers.TryGetValues("X-Api-Key", out var values)
            ? values.Single()
            : string.Empty;
        Requests.Add(new RecordedHttpRequest(request.Method,
                                             request.RequestUri ?? throw new InvalidOperationException("Missing request URI."),
                                             body,
                                             contentType,
                                             apiKey));

        if (mResponses.Count == 0)
            throw new InvalidOperationException("No scripted HTTP response remains.");

        return await mResponses.Dequeue()(request, cancellationToken);
    }
}

internal sealed class ScriptedDoclingClient : IDoclingClient
{
    private readonly Queue<DoclingServiceObservation> mHealth = new();
    private readonly Queue<DoclingServiceObservation> mReadiness = new();
    private readonly Queue<DoclingConversionResult> mConversions = new();

    public int HealthCalls { get; private set; }
    public int ReadinessCalls { get; private set; }
    public int ConversionCalls { get; private set; }
    public DoclingFile? LastConvertedFile { get; private set; }

    public void EnqueueHealth(params DoclingServiceObservation[] results)
    {
        foreach(var result in results)
            mHealth.Enqueue(result);
    }

    public void EnqueueReadiness(params DoclingServiceObservation[] results)
    {
        foreach(var result in results)
            mReadiness.Enqueue(result);
    }

    public void EnqueueConversions(params DoclingConversionResult[] results)
    {
        foreach(var result in results)
            mConversions.Enqueue(result);
    }

    public Task<DoclingServiceObservation> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HealthCalls++;
        return Task.FromResult(mHealth.Dequeue());
    }

    public Task<DoclingServiceObservation> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadinessCalls++;
        return Task.FromResult(mReadiness.Dequeue());
    }

    public Task<DoclingConversionResult> ConvertAsync(DoclingFile file,
                                                      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        ConversionCalls++;
        LastConvertedFile = file;
        return Task.FromResult(mConversions.Dequeue());
    }
}

internal sealed class MutableDoclingTimeProvider : TimeProvider
{
    public MutableDoclingTimeProvider(DateTimeOffset utcNow)
    {
        mUtcNow = utcNow;
    }

    private DateTimeOffset mUtcNow;

    public override DateTimeOffset GetUtcNow() => mUtcNow;

    public void Advance(TimeSpan duration)
    {
        mUtcNow += duration;
    }
}
