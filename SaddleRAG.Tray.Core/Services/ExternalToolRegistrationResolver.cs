// ExternalToolRegistrationResolver.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Decides what to record when someone supplies tool paths — the installer dialog
///     today, any other caller later. Precedence is explicit input, then whatever is
///     already registered, then detection. Detection is the last resort and only ever
///     supplies a default; it never overrides a choice the user already made.
/// </summary>
public static class ExternalToolRegistrationResolver
{
    public static ExternalToolRegistration Resolve(ExternalToolRegistration current,
                                                   ExternalToolDetector detector,
                                                   string? doclingCommand,
                                                   string? doclingArguments,
                                                   string? tesseractDirectory,
                                                   string? tessdataDirectory)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(detector);

        return new ExternalToolRegistration(ResolveDocling(current, detector, doclingCommand, doclingArguments),
                                            ResolveTesseract(current,
                                                             detector,
                                                             tesseractDirectory,
                                                             tessdataDirectory));
    }

    private static DoclingRegistration? ResolveDocling(ExternalToolRegistration current,
                                                       ExternalToolDetector detector,
                                                       string? command,
                                                       string? arguments)
    {
        DoclingRegistration? result;
        if (string.IsNullOrWhiteSpace(command))
            result = current.Docling ?? detector.DetectDocling();
        else
            result = new DoclingRegistration(command.Trim(),
                                             arguments?.Trim() ?? string.Empty,
                                             Path.GetDirectoryName(command.Trim()) ?? string.Empty,
                                             current.Docling?.Environment);

        return result;
    }

    private static TesseractRegistration? ResolveTesseract(ExternalToolRegistration current,
                                                            ExternalToolDetector detector,
                                                            string? directory,
                                                            string? tessdata)
    {
        TesseractRegistration? result;
        if (string.IsNullOrWhiteSpace(directory))
        {
            result = current.Tesseract ?? detector.DetectTesseract();
        }
        else
        {
            string executableDirectory = directory.Trim();
            string resolvedTessdata = string.IsNullOrWhiteSpace(tessdata)
                                          ? Path.Combine(executableDirectory, TessdataFolderName)
                                          : tessdata.Trim();
            result = new TesseractRegistration(executableDirectory, resolvedTessdata);
        }

        return result;
    }

    private const string TessdataFolderName = "tessdata";
}
