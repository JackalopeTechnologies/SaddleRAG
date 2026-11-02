// ServiceImagePath.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.RegularExpressions;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Decides which program a Windows service runs from its registry <c>ImagePath</c>, using exactly
///     the rule in <c>Restart-SaddleRAGComponent.ps1</c> (SaddleRAG.Installer). The tray must never
///     offer a service that script would refuse, so any change here must be made there too.
///     <para>
///         The rule: a quoted path is the text inside the quotes; an unquoted path is only the first
///         token, ended by a space or a tab (never a Unicode space). Text after the program, such as
///         <c>-log C:\data\mongod.exe</c>, is never considered. A path with characters that cannot
///         appear in a Windows path names no program.
///     </para>
/// </summary>
public static class ServiceImagePath
{
    /// <summary>
    ///     The file name of the program an <c>ImagePath</c> launches, or null when the text cannot be
    ///     read unambiguously.
    /// </summary>
    public static string? ExecutableFileName(string? imagePath)
    {
        string? result = null;
        if (imagePath is not null)
        {
            Match match = smImagePathPattern.Match(imagePath);
            if (match.Success)
                result = FileNameOf(match.Groups[PathGroupName].Value);
        }

        return result;
    }

    /// <summary>
    ///     The names of the services whose program has the file name <paramref name="executableFileName" />
    ///     (compared ignoring case), in ordinal name order. A service whose name the elevated script would
    ///     refuse is left out, so the first name is always one the script accepts.
    /// </summary>
    public static IReadOnlyList<string> ServicesRunning(IEnumerable<KeyValuePair<string, string?>> services,
                                                        string executableFileName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableFileName);

        List<string> result = services.Where(service => smServiceNamePattern.IsMatch(service.Key))
                                      .Where(service => string.Equals(ExecutableFileName(service.Value),
                                                                      executableFileName,
                                                                      StringComparison.OrdinalIgnoreCase))
                                      .Select(service => service.Key)
                                      .Order(StringComparer.Ordinal)
                                      .ToList();
        return result;
    }

    // Matches the file-name split of [System.IO.Path]::GetFileName in Windows PowerShell (the elevated
    // script's runtime), independent of the OS the tests run on: the text after the last backslash,
    // slash, or colon. That runtime also throws on a path containing a double quote, <, >, | or a
    // control character, which the script turns into "not allow-listed"; null is the same answer here.
    private static string? FileNameOf(string path)
    {
        string? result = path.Any(IsIllegalPathCharacter)
                             ? null
                             : path[(path.LastIndexOfAny(smPathSeparators) + 1)..];
        return result;
    }

    private static bool IsIllegalPathCharacter(char character) =>
        character < FirstPrintableCharacter || IllegalPathCharacters.Contains(character);

    private const string PathGroupName = "path";
    private const string IllegalPathCharacters = "\"<>|";
    private const char FirstPrintableCharacter = ' ';

    // Copied verbatim from $ImagePathPattern in Restart-SaddleRAGComponent.ps1.
    private const string ImagePathPatternText = "^[ \\t]*(?:\"(?<path>[^\"]+)\"|(?<path>[^\"\\s]+))(?=[ \\t]|\\z)";

    // Copied verbatim from $ServiceNamePattern in Restart-SaddleRAGComponent.ps1.
    private const string ServiceNamePatternText = "^[A-Za-z0-9_.-]+\\z";

    private static readonly char[] smPathSeparators = ['\\', '/', ':'];
    private static readonly Regex smImagePathPattern = new(ImagePathPatternText, RegexOptions.None);
    private static readonly Regex smServiceNamePattern = new(ServiceNamePatternText, RegexOptions.None);
}
