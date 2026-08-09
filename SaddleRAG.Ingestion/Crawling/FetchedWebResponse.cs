// FetchedWebResponse.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Collections.ObjectModel;

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Immutable snapshot of one acquired web response. Mutable header and
///     body inputs are copied at the acquisition boundary so classification,
///     extraction, and artifact persistence all observe the same bytes.
/// </summary>
public sealed class FetchedWebResponse
{
    public FetchedWebResponse(string OriginalUrl,
                              string AttemptedUrl,
                              string FinalUrl,
                              int StatusCode,
                              IReadOnlyDictionary<string, string> Headers,
                              ReadOnlyMemory<byte> Body)
    {
        ArgumentException.ThrowIfNullOrEmpty(OriginalUrl);
        ArgumentException.ThrowIfNullOrEmpty(AttemptedUrl);
        ArgumentException.ThrowIfNullOrEmpty(FinalUrl);
        ArgumentOutOfRangeException.ThrowIfNegative(StatusCode);
        ArgumentNullException.ThrowIfNull(Headers);

        var copiedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach(KeyValuePair<string, string> header in Headers)
            copiedHeaders[header.Key] = header.Value;

        this.OriginalUrl = OriginalUrl;
        this.AttemptedUrl = AttemptedUrl;
        this.FinalUrl = FinalUrl;
        this.StatusCode = StatusCode;
        this.Headers = new ReadOnlyDictionary<string, string>(copiedHeaders);
        this.Body = Body.ToArray();
    }

    public string OriginalUrl { get; }

    public string AttemptedUrl { get; }

    public string FinalUrl { get; }

    public int StatusCode { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public ReadOnlyMemory<byte> Body { get; }
}
