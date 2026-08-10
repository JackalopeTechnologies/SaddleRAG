// IServerLogAcknowledgement.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     How far through the server log an operator has already read. The Logs nav badge
///     is a call to action, so viewing the Logs page has to answer it; without this the
///     badge nagged for a rolling hour no matter how many times the entries were read.
///     <para>
///         Process-wide and shared by every monitor circuit, matching the single operator
///         the Monitor is built for.
///     </para>
/// </summary>
public interface IServerLogAcknowledgement
{
    /// <summary>
    ///     Timestamp of the newest log entry already put in front of an operator, or null
    ///     when the Logs page has not been read since the server started.
    /// </summary>
    DateTimeOffset? AcknowledgedThrough { get; }

    /// <summary>
    ///     Record that everything up to <paramref name="timestamp" /> has been seen.
    ///     Only ever moves forward, so a stale browser circuit cannot un-acknowledge
    ///     what a newer one already showed.
    /// </summary>
    void AcknowledgeThrough(DateTimeOffset timestamp);
}
