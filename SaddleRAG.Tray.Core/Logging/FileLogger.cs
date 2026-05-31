// FileLogger.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Tray.Core.Logging;

internal sealed class FileLogger : ILogger
{
    public FileLogger(string category, FileLoggerProvider provider)
    {
        mCategory = category;
        mProvider = provider;
    }

    private readonly string mCategory;
    private readonly FileLoggerProvider mProvider;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel,
                            EventId eventId,
                            TState state,
                            Exception? exception,
                            Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (IsEnabled(logLevel))
        {
            string message = formatter(state, exception);
            var line = $"{DateTimeOffset.Now:O} [{logLevel}] {mCategory}: {message}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            mProvider.Append(line + Environment.NewLine);
        }
    }
}
