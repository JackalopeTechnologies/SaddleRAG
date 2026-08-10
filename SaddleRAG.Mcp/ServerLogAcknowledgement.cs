// ServerLogAcknowledgement.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Interfaces;

#endregion

namespace SaddleRAG.Mcp;

/// <summary>
///     In-memory <see cref="IServerLogAcknowledgement" /> registered as a singleton.
///     Deliberately not persisted: a restarted server has a fresh log to answer for, so
///     the badge should speak up again.
/// </summary>
public sealed class ServerLogAcknowledgement : IServerLogAcknowledgement
{
    /// <inheritdoc />
    public DateTimeOffset? AcknowledgedThrough
    {
        get
        {
            lock(mGate)
                return mAcknowledgedThrough;
        }
    }

    /// <inheritdoc />
    public void AcknowledgeThrough(DateTimeOffset timestamp)
    {
        lock(mGate)
        {
            if (mAcknowledgedThrough == null || timestamp > mAcknowledgedThrough)
                mAcknowledgedThrough = timestamp;
        }
    }

    private readonly Lock mGate = new();

    private DateTimeOffset? mAcknowledgedThrough;
}
