// ArgumentQuoteEscapeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Installer.Logic;

#endregion

namespace SaddleRAG.Tests.Installer;

/// <summary>
///     Pins the <see cref="ArgumentQuoteEscape.EscapeForCommandLine" />
///     algorithm against the documented <c>CommandLineToArgvW</c> rules
///     and the JScript mirror in
///     <c>SaddleRAG.Installer/EscapeAppSettingsProperties.js</c>. The
///     JScript runs in production; the C# is the unit-testable source of
///     truth. A divergence in algorithm between the two sides is the
///     exact silent-fail mode this test class exists to catch.
/// </summary>
public sealed class ArgumentQuoteEscapeTests
{
    [Theory]
    [InlineData("",                       "")]
    [InlineData("plain",                  "plain")]
    [InlineData("mongodb://host:27017",   "mongodb://host:27017")]
    [InlineData("password\"with-quote",   "password\\\"with-quote")]
    [InlineData("C:\\path\\file",         "C:\\path\\file")]
    [InlineData("ends-in-backslash\\",    "ends-in-backslash\\\\")]
    [InlineData("ends-in-double\\\\",     "ends-in-double\\\\\\\\")]
    [InlineData("\\\"escaped",            "\\\\\\\"escaped")]
    [InlineData("multi\"\"quotes",        "multi\\\"\\\"quotes")]
    public void EscapeForCommandLineMatchesCommandLineToArgvWRules(string input, string expected)
    {
        string actual = ArgumentQuoteEscape.EscapeForCommandLine(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EscapeForCommandLineTreatsNullAsEmpty()
    {
        string actual = ArgumentQuoteEscape.EscapeForCommandLine(null);
        Assert.Equal(string.Empty, actual);
    }

    [Fact]
    public void JScriptMirrorImplementsTheSameAlgorithm()
    {
        // Pins the cross-language contract by asserting the JScript source
        // contains the load-bearing pieces of the algorithm: the function
        // name itself, the per-character backslash counter, the
        // double-the-run-when-followed-by-quote branch, and the trailing
        // double-the-run-before-implicit-close branch. The string-level
        // match catches the most common drift (someone simplifies one side
        // by reverting to `value.split('"').join('\"')`).
        string? jsPath = InstallerSourceTreeResolver.TryResolveInstallerFile(JScriptFileName);
        if (jsPath == null)
            Assert.Skip(JScriptMissingSkipReason);
        else
        {
            string jsText = File.ReadAllText(jsPath);
            Assert.Contains(JScriptFunctionToken, jsText);
            Assert.Contains(JScriptBackslashCounterToken, jsText);
            Assert.Contains(JScriptDoubleRunToken, jsText);
        }
    }

    private const string JScriptFileName = "EscapeAppSettingsProperties.js";
    private const string JScriptFunctionToken = "_escapeForCommandLine";
    private const string JScriptBackslashCounterToken = "_backslashRun++";
    private const string JScriptDoubleRunToken = "_backslashRun * 2";
    private const string JScriptMissingSkipReason =
        "EscapeAppSettingsProperties.js not locatable from test binary directory; skipping mirror check.";
}
