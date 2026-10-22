// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>Routes native diagnostics without changing inference or graph capture.</summary>
internal static partial class OnnxRuntimeDiagnostics
{
    internal static bool EnsureInitialized(ILogger? logger = null)
    {
        lock (smInitializationLock)
        {
            psLogger ??= logger;
            if (!OrtEnv.IsCreated)
            {
                var options = new EnvironmentCreationOptions
                {
                    logId = NativeLogId,
                    logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
                    loggingFunction = smLoggingFunction
                };
                OrtEnv.CreateInstanceWithOptions(ref options);
                psOwnsLogging = true;
            }
        }
        return psOwnsLogging;
    }

    internal static LogLevel Classify(OrtLoggingLevel severity, string category, string location, string message)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(message);
        LogLevel level = severity switch
        {
            OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE => LogLevel.Trace,
            OrtLoggingLevel.ORT_LOGGING_LEVEL_INFO => LogLevel.Information,
            OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING => LogLevel.Warning,
            OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR => LogLevel.Error,
            OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL => LogLevel.Critical,
            _ => LogLevel.Warning
        };

        // GenAI 0.14.1 State::Run intentionally replaces gpu_graph_id on each token.
        // ORT 1.24.4 ConfigOptions::AddConfigEntry warns even for an unchanged value.
        // No compatible DirectML package fixes this third-party false positive.
        // Scope the diagnostic reclassification to this exact source/key/message;
        // retain every other warning/error and leave graph capture enabled. Issue 189.
        // https://github.com/microsoft/onnxruntime-genai/blob/a30f479af016cb098688726831a9acbb8d19f0b2/src/models/model.cpp#L84-L92
        if (severity == OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING &&
            category == NativeCategory && ConfigEntryLocation().IsMatch(location) &&
            IsGraphIdReplacement(message))
            level = LogLevel.Debug;
        return level;
    }

    private static bool IsGraphIdReplacement(string message)
    {
        Match match = GraphIdReplacement().Match(message);
        bool expected = match.Success &&
                        int.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign,
                            CultureInfo.InvariantCulture, out int graphId) && graphId >= DisabledGraphId;
        return expected;
    }

    private static void Receive(IntPtr state, OrtLoggingLevel severity, string category,
        string logId, string location, string message)
    {
        try
        {
            LogLevel level = Classify(severity, category, location, message);
            ILogger? logger = Volatile.Read(ref psLogger);
            if (logger != null)
                logger.Log(level, NativeMessageFormat, category, logId, location, message);
            else
                WriteFallback(level, category, logId, location, message);
        }
        catch (Exception exception)
        {
            // A managed logging failure must never unwind through the native callback.
            // Preserve the original diagnostic on stderr when the application sink fails.
            try
            {
                Console.Error.WriteLine($"Native diagnostic routing failed: {exception.Message}; {location}: {message}");
            }
            catch (Exception)
            {
                // stderr can be unavailable during process shutdown; no safe sink remains.
            }
        }
    }

    private static void WriteFallback(LogLevel level, string category, string logId, string location, string message)
    {
        string diagnostic = $"[{level}:{category}:{logId}, {location}] {message}";
        if (level < LogLevel.Warning)
            Trace.WriteLine(diagnostic);
        else
            Console.Error.WriteLine(diagnostic);
    }

    [GeneratedRegex(ConfigEntryLocationPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ConfigEntryLocation();

    [GeneratedRegex(GraphIdReplacementPattern, RegexOptions.CultureInvariant)]
    private static partial Regex GraphIdReplacement();

    private static readonly Lock smInitializationLock = new();
    private static readonly DOrtLoggingFunction smLoggingFunction = Receive;
    private static ILogger? psLogger;
    private static bool psOwnsLogging;
    private const int DisabledGraphId = -1;
    private const string NativeLogId = "SaddleRAG.Native";
    private const string NativeCategory = "onnxruntime";
    private const string NativeMessageFormat = "Native {Category} ({NativeLogId}) {Location}: {Message}";
    private const string ConfigEntryLocationPattern = @"(?:^|[/\\])config_options\.cc:\d+ (?:onnxruntime::)?ConfigOptions::AddConfigEntry$";
    private const string GraphIdReplacementPattern = @"^Config with key \[gpu_graph_id\] already exists with value \[(-1|[0-9]{1,10})\]\. It will be overwritten$";
}
