// RecordingOllamaHandler.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using System.Text;

#endregion

namespace SaddleRAG.Tests.Embedding;

/// <summary>
///     Stands in for an Ollama server: records each request body by path and
///     answers with a canned body, so tests can see exactly what SaddleRAG sends.
/// </summary>
internal sealed class RecordingOllamaHandler : HttpMessageHandler
{
    public RecordingOllamaHandler(string responseBody)
    {
        mResponseBody = responseBody;
    }

    private readonly string mResponseBody;

    public string LastBody { get; private set; } = string.Empty;

    public string LastPath { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                 CancellationToken cancellationToken)
    {
        LastPath = request.RequestUri?.AbsolutePath ?? string.Empty;
        LastBody = request.Content == null
                       ? string.Empty
                       : await request.Content.ReadAsStringAsync(cancellationToken);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
                           {
                               Content = new StringContent(mResponseBody, Encoding.UTF8, "application/json")
                           };
        return response;
    }
}
