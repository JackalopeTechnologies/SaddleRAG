// ArgumentQuoteEscape.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

using System.Text;

namespace SaddleRAG.Installer.Logic;

/// <summary>
///     Implements the <c>CommandLineToArgvW</c>-compatible escape used by
///     the installer's <c>EscapeAppSettingsProperties.js</c> custom action
///     to pre-quote MSI property values before they're substituted into
///     the deferred PatchAppSettings <c>powershell.exe</c> command line.
///     The MSI runs JScript, not .NET, so the live installer cannot call
///     this class — the algorithm is mirrored in
///     <c>EscapeAppSettingsProperties.js</c> and the two must agree.
///     Tests at <c>SaddleRAG.Tests/Installer/ArgumentQuoteEscapeTests.cs</c>
///     pin the C# side and assert the JScript source still contains the
///     mirror function.
/// </summary>
/// <remarks>
///     The Microsoft-documented argument-parsing rules for the legacy
///     CRT / <c>CommandLineToArgvW</c> path:
///     <list type="bullet">
///         <item>
///             Inside a quoted argument, a run of <c>2n</c> backslashes
///             followed by a <c>"</c> produces <c>n</c> literal backslashes
///             and the <c>"</c> closes the quoted span.
///         </item>
///         <item>
///             A run of <c>2n+1</c> backslashes followed by <c>"</c>
///             produces <c>n</c> literal backslashes followed by a literal
///             <c>"</c> (the trailing <c>\</c> escapes the quote and the
///             span continues).
///         </item>
///         <item>
///             A trailing run of backslashes that sits immediately before
///             the implicit close-quote (which the caller wraps the value
///             in) follows the same 2n-doubling rule.
///         </item>
///     </list>
///     The <see cref="EscapeForCommandLine" /> method walks the input
///     once and dispatches each character through a switch expression
///     that emits the right number of backslashes and updates the
///     pending-run counter; a final flush handles the trailing-run case.
/// </remarks>
public static class ArgumentQuoteEscape
{
    /// <summary>
    ///     Returns an escaped form of <paramref name="value" /> safe to
    ///     interpolate inside a <c>"..."</c>-wrapped argv token on a
    ///     Windows command line parsed by <c>CommandLineToArgvW</c>. The
    ///     caller is responsible for adding the surrounding double
    ///     quotes; this method only escapes the contents.
    /// </summary>
    public static string EscapeForCommandLine(string? value)
    {
        string source = value ?? string.Empty;
        var result = new StringBuilder(source.Length);
        int backslashRun = 0;

        for (int i = 0; i < source.Length; i++)
            backslashRun = ProcessChar(source[i], result, backslashRun);

        // Trailing backslashes sit immediately before the caller's wrapping
        // " character, so they need the same 2n-doubling rule that adjacent
        // backslash runs get.
        FlushTrailingBackslashes(result, backslashRun);

        return result.ToString();
    }

    private static int ProcessChar(char ch, StringBuilder result, int pendingBackslashes) => ch switch
    {
        BackslashChar => pendingBackslashes + 1,
        QuoteChar     => EmitEscapedQuote(result, pendingBackslashes),
        var _         => EmitLiteralChar(result, ch, pendingBackslashes)
    };

    private static int EmitEscapedQuote(StringBuilder result, int pendingBackslashes)
    {
        result.Append(BackslashChar, pendingBackslashes * 2);
        result.Append(BackslashChar);
        result.Append(QuoteChar);
        return 0;
    }

    private static int EmitLiteralChar(StringBuilder result, char ch, int pendingBackslashes)
    {
        FlushBackslashesVerbatim(result, pendingBackslashes);
        result.Append(ch);
        return 0;
    }

    private static void FlushBackslashesVerbatim(StringBuilder result, int count)
    {
        if (count > 0)
            result.Append(BackslashChar, count);
    }

    private static void FlushTrailingBackslashes(StringBuilder result, int count)
    {
        if (count > 0)
            result.Append(BackslashChar, count * 2);
    }

    private const char BackslashChar = '\\';
    private const char QuoteChar = '"';
}
