// RecoveryFakeDatabase.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>
///     A database that refuses pings until <see cref="BeginAnswering" />, and, like the
///     real context, stops being known down once a ping succeeds.
/// </summary>
internal sealed class RecoveryFakeDatabase : IRecoverableDatabase
{
    public RecoveryFakeDatabase(bool isLocal, bool isKnownDown)
    {
        IsLocal = isLocal;
        mIsKnownDown = isKnownDown;
    }

    private volatile bool mAnswering;
    private volatile bool mIsKnownDown;
    private int mPingCount;
    private volatile bool mSawFailFastScope;

    /// <summary>What a refused ping throws; null for the usual "no answer yet" timeout.</summary>
    public Exception? PingFailure { get; init; }

    public int PingCount => Volatile.Read(ref mPingCount);

    public bool SawFailFastScope => mSawFailFastScope;

    public string Endpoint => "localhost:27017";

    public bool IsLocal { get; }

    public bool IsKnownDown => mIsKnownDown;

    public Task CheckHealthAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref mPingCount);
        if (DatabaseFailFastScope.IsActive)
            mSawFailFastScope = true;

        Task result;
        if (mAnswering)
        {
            mIsKnownDown = false;
            result = Task.CompletedTask;
        }
        else
            result = Task.FromException(PingFailure ?? new TimeoutException("No answer."));

        return result;
    }

    public void BeginAnswering() => mAnswering = true;
}
