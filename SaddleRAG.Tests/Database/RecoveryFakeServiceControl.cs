// RecoveryFakeServiceControl.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>A Windows service whose reported states and start outcome the test chooses.</summary>
internal sealed class RecoveryFakeServiceControl : IMongoServiceControl
{
    public RecoveryFakeServiceControl(MongoServiceState initial, params MongoServiceState[] laterStates)
    {
        mState = initial;
        mPending = new Queue<MongoServiceState>([initial, .. laterStates]);
    }

    private readonly Lock mLock = new Lock();
    private readonly Queue<MongoServiceState> mPending;
    private int mGetStateCount;
    private int mStartCount;
    private MongoServiceState mState;

    public Action? OnStart { get; init; }

    public Exception? StartFailure { get; init; }

    public Exception? GetStateFailure { get; init; }

    /// <summary>The service accepts a start and then stops again, as one that crashes on start does.</summary>
    public bool StopsAgainAfterStart { get; init; }

    public int GetStateCount => Volatile.Read(ref mGetStateCount);

    public int StartCount => Volatile.Read(ref mStartCount);

    public MongoServiceState GetState(string serviceName)
    {
        Interlocked.Increment(ref mGetStateCount);
        if (GetStateFailure != null)
            throw GetStateFailure;

        MongoServiceState result;
        lock(mLock)
        {
            if (mPending.Count > 0)
                mState = mPending.Dequeue();
            result = mState;
        }

        return result;
    }

    public void Start(string serviceName)
    {
        Interlocked.Increment(ref mStartCount);
        if (StartFailure != null)
            throw StartFailure;

        lock(mLock)
        {
            mPending.Clear();
            mState = StopsAgainAfterStart ? MongoServiceState.Stopped : MongoServiceState.Running;
        }

        OnStart?.Invoke();
    }
}
