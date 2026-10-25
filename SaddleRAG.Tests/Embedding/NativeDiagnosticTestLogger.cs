// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SaddleRAG.Tests.Embedding;

internal sealed class NativeDiagnosticTestLogger : ILogger
{
    internal ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
    internal bool ThrowOnLog { get; init; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (ThrowOnLog)
            throw new IOException("Test logging sink unavailable");
        Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
