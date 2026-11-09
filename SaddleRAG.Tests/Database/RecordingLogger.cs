// RecordingLogger.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>Records what a component logs, so tests can count and read messages by level.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly Lock mLock = new Lock();
    private readonly List<(LogLevel Level, string Message)> mEntries = [];

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
            mEntries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>The formatted text of every message logged at <paramref name="level" />.</summary>
    public IReadOnlyList<string> Messages(LogLevel level)
    {
        IReadOnlyList<string> result;
        lock(mLock)
        {
            result = mEntries.Where(entry => entry.Level == level).Select(entry => entry.Message).ToList();
        }

        return result;
    }

    public int Count(LogLevel level)
    {
        int result;
        lock(mLock)
        {
            result = mEntries.Count(entry => entry.Level == level);
        }

        return result;
    }
}
