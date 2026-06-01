// JsonlReader.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Runtime.CompilerServices;
using System.Text.Json;

#endregion

namespace SaddleRAG.Packaging.Internal;

/// <summary>
///     Reads one JSON object per line from an underlying stream. Malformed
///     lines abort with a JsonException whose message includes the
///     1-based line number, matching the spec's "report file + line" rule.
/// </summary>
public sealed class JsonlReader<T>
{
    public JsonlReader(Stream stream, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        mStream = stream;
        mOptions = options ?? BundleJsonOptions.JsonlDefault;
    }

    private readonly Stream mStream;
    private readonly JsonSerializerOptions mOptions;

    /// <summary>
    ///     Lazily reads all lines, deserializing each to <typeparamref name="T" />.
    ///     Blank lines are skipped. Malformed JSON throws <see cref="JsonException" />
    ///     with a message that includes the 1-based line number.
    /// </summary>
    public async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(mStream, leaveOpen: true);
        int lineNumber = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            lineNumber++;
            if (!string.IsNullOrWhiteSpace(line))
            {
                T item;
                try
                {
                    item = JsonSerializer.Deserialize<T>(line, mOptions)
                           ?? throw new JsonException($"line {lineNumber}: deserialized to null");
                }
                catch (JsonException ex)
                {
                    throw new JsonException($"line {lineNumber}: {ex.Message}", ex);
                }

                yield return item;
            }
        }
    }
}
