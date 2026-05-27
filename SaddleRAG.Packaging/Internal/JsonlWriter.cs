// JsonlWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json;

#endregion

namespace SaddleRAG.Packaging.Internal;

/// <summary>
///     Writes one JSON object per line to an underlying stream. Async-disposable
///     so the buffer flushes deterministically. UTF-8 throughout; no BOM.
/// </summary>
public sealed class JsonlWriter<T> : IAsyncDisposable, IDisposable
{
    public JsonlWriter(Stream stream, bool leaveOpen = false, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        mStream = stream;
        mLeaveOpen = leaveOpen;
        mOptions = options ?? BundleJsonOptions.JsonlDefault;
    }

    private static readonly byte[] smNewline = "\n"u8.ToArray();

    private readonly Stream mStream;
    private readonly bool mLeaveOpen;
    private readonly JsonSerializerOptions mOptions;
    private bool mDisposed;

    /// <summary>
    ///     Serializes <paramref name="item" /> as a single JSON line and appends a newline.
    /// </summary>
    public async Task WriteAsync(T item, CancellationToken ct = default)
    {
        await JsonSerializer.SerializeAsync(mStream, item, mOptions, ct);
        await mStream.WriteAsync(smNewline, ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!mDisposed)
        {
            mDisposed = true;
            await mStream.FlushAsync();
            if (!mLeaveOpen)
                await mStream.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!mDisposed)
        {
            mDisposed = true;
            mStream.Flush();
            if (!mLeaveOpen)
                mStream.Dispose();
        }
    }
}
