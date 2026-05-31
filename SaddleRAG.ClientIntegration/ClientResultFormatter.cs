// ClientResultFormatter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.ClientIntegration;

/// <summary>
///     Pure, side-effect-free formatting of registrar results into the one-line-per-agent
///     summaries shown in the tray. Kept separate from the tray service so the format logic
///     is unit-testable against hand-built results without touching the filesystem.
/// </summary>
public static class ClientResultFormatter
{
    public sealed record StatusLineInput(string DisplayName, StatusResult Status, bool IsDetected);

    public static string SummarizeRegister(RegistrarResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        IEnumerable<string> lines = result.RegisterResults.Select(FormatRegisterLine);
        return string.Join(Environment.NewLine, lines);
    }

    public static string SummarizeUnregister(RegistrarResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        IEnumerable<string> lines = result.UnregisterResults.Select(FormatUnregisterLine);
        return string.Join(Environment.NewLine, lines);
    }

    public static string SummarizeStatus(IReadOnlyList<StatusLineInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        IEnumerable<string> lines = inputs.Select(FormatStatusLine);
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatRegisterLine(RegisterResult result)
    {
        string prefix = result switch
        {
            { Skipped: true } => SkipPrefix,
            { Success: true } => OkPrefix,
            _ => ErrPrefix
        };
        return $"{prefix} {result.ClientName}: {result.Message}";
    }

    private static string FormatUnregisterLine(UnregisterResult result)
    {
        string prefix = result switch
        {
            { Success: true, WasNoOp: true } => NoopPrefix,
            { Success: true } => OkPrefix,
            _ => ErrPrefix
        };
        return $"{prefix} {result.ClientName}: {result.Message}";
    }

    private static string FormatStatusLine(StatusLineInput input)
    {
        string state = input.Status.SaddleRagEntryPresent ? RegisteredText : NotRegisteredText;
        string detected = input.IsDetected ? DetectedSuffix : string.Empty;
        return $"{input.DisplayName}: {state}{detected}";
    }

    private const string OkPrefix = "OK ";
    private const string ErrPrefix = "ERR";
    private const string SkipPrefix = "SKIP";
    private const string NoopPrefix = "NOOP";
    private const string RegisteredText = "registered";
    private const string NotRegisteredText = "not registered";
    private const string DetectedSuffix = " (detected)";
}
