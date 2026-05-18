// DrainStage.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Threading.Channels;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Sinks an embed-stage output channel without doing anything with the
///     batches. Used by the dry-run orchestrator path in place of
///     <see cref="IndexStage" /> so the embed stage's bounded writer
///     doesn't block. Does not touch repositories, the vector index, or
///     the audit log — those are IndexStage's responsibilities.
/// </summary>
internal sealed class DrainStage
{
    public async Task RunAsync(ChannelReader<DocChunk[]> input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        await foreach(var _ in input.ReadAllAsync(ct))
        {
            // Intentionally empty: dry-run discards embedded chunks
            // because IndexStage is not in the pipeline.
        }
    }
}
