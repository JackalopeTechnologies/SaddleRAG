// EmbeddingResponseHandler.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using System.Text.Json;

#endregion

namespace SaddleRAG.Tests.Embedding;

internal sealed class EmbeddingResponseHandler : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    public Func<HttpResponseMessage>? Response { get; set; }
    private int mVectorCount;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        Requests.Add(body);
        using var parsed = JsonDocument.Parse(body);
        var vectors = new List<float[]>();
        for (var index = 0; index < parsed.RootElement.GetProperty("input").GetArrayLength(); index++)
            vectors.Add([++mVectorCount, 1]);
        HttpResponseMessage result = Response?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { embeddings = vectors }))
        };
        return result;
    }
}
