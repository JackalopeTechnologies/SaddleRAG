// RecordingLogger.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>Counts what a component logs at each level.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly Lock mLock = new Lock();
    private readonly List<LogLevel> mLevels = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel,
                            EventId eventId,
                            TState state,
                            Exception? exception,
                            Func<TState, Exception?, string> formatter)
    {
        lock(mLock)
        {
            mLevels.Add(logLevel);
        }
    }

    public int Count(LogLevel level)
    {
        int result;
        lock(mLock)
        {
            result = mLevels.Count(l => l == level);
        }

        return result;
    }
}
