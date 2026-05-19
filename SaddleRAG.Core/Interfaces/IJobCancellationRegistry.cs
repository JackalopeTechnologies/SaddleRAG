// IJobCancellationRegistry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Process-wide map from <c>jobId</c> to its in-flight
///     <see cref="CancellationTokenSource" />. Every job runner that
///     produces cancellable work (per
///     <see cref="Models.Monitor.JobTypeCapabilities.IsCancellable" />)
///     registers on start and unregisters in a finally block. The
///     <c>cancel_job</c> MCP tool consults this registry instead of
///     reaching into any single runner.
/// </summary>
public interface IJobCancellationRegistry
{
    /// <summary>
    ///     Associate <paramref name="jobId" /> with <paramref name="cts" />.
    ///     Idempotent — re-registering replaces the prior entry. The
    ///     caller retains ownership of <paramref name="cts" /> and is
    ///     responsible for disposing it after <see cref="Unregister" />.
    /// </summary>
    void Register(string jobId, CancellationTokenSource cts);

    /// <summary>
    ///     Remove <paramref name="jobId" /> from the registry. Does not
    ///     dispose the underlying <see cref="CancellationTokenSource" />.
    ///     No-op if the id is unknown.
    /// </summary>
    void Unregister(string jobId);

    /// <summary>
    ///     If <paramref name="jobId" /> is registered, signal cancellation
    ///     on its token source and return <c>true</c>. Returns <c>false</c>
    ///     if the id is unknown (job already finished, was never started,
    ///     or this process restarted while the job was Running).
    /// </summary>
    Task<bool> TryCancelAsync(string jobId);
}
