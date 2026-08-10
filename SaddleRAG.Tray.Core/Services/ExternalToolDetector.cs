// ExternalToolDetector.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Finds where the user already installed Docling and Tesseract, so a registration
///     dialog can be pre-filled. Detection only ever supplies defaults for the user to
///     confirm — it never writes to <see cref="ExternalToolRegistry" /> on its own.
/// </summary>
public sealed class ExternalToolDetector
{
    public ExternalToolDetector(IFileSystemProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        mProbe = probe;
    }

    private readonly IFileSystemProbe mProbe;

    /// <summary>First hit wins: user start script, then the venv executable, then PATH.</summary>
    public DoclingRegistration? DetectDocling()
    {
        string doclingRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                          ApplicationsFolderName,
                                          DoclingFolderName);
        string startScript = Path.Combine(doclingRoot, StartScriptFileName);
        DoclingRegistration? result = mProbe.FileExists(startScript) ? FromStartScript(startScript) : null;
        if (result == null)
        {
            string virtualEnvExecutable = Path.Combine(doclingRoot,
                                                       VirtualEnvFolderName,
                                                       VirtualEnvScriptsFolderName,
                                                       DoclingServeFileName);
            string? executable = mProbe.FileExists(virtualEnvExecutable)
                                     ? virtualEnvExecutable
                                     : mProbe.FindOnPath(DoclingServeFileName);
            result = executable == null ? null : FromExecutable(executable);
        }

        return result;
    }

    /// <summary>First hit wins: 64-bit Program Files, then 32-bit, then PATH.</summary>
    public TesseractRegistration? DetectTesseract()
    {
        string? executable = FindTesseractExecutable();
        TesseractRegistration? result = null;
        if (executable != null)
        {
            string executableDirectory = Path.GetDirectoryName(executable) ?? string.Empty;
            string tessdataDirectory = Path.Combine(executableDirectory, TessdataFolderName);
            result = new TesseractRegistration(executableDirectory,
                                               mProbe.DirectoryExists(tessdataDirectory)
                                                   ? tessdataDirectory
                                                   : string.Empty);
        }

        return result;
    }

    private string? FindTesseractExecutable()
    {
        string programFiles = TesseractPathUnder(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = TesseractPathUnder(Environment.SpecialFolder.ProgramFilesX86);
        string? result = mProbe.FileExists(programFiles) ? programFiles : null;
        result ??= mProbe.FileExists(programFilesX86) ? programFilesX86 : null;
        result ??= mProbe.FindOnPath(TesseractFileName);
        return result;
    }

    private static string TesseractPathUnder(Environment.SpecialFolder folder) =>
        Path.Combine(Environment.GetFolderPath(folder), TesseractFolderName, TesseractFileName);

    private static DoclingRegistration FromStartScript(string scriptPath) =>
        new(PowerShellCommand,
            $"{PowerShellScriptArguments} \"{scriptPath}\"",
            Path.GetDirectoryName(scriptPath) ?? string.Empty,
            Environment: null);

    private static DoclingRegistration FromExecutable(string executablePath) =>
        new(executablePath,
            DoclingServeArguments,
            Path.GetDirectoryName(executablePath) ?? string.Empty,
            Environment: null);

    private const string ApplicationsFolderName = "Applications";
    private const string DoclingFolderName = "Docling";
    private const string StartScriptFileName = "start-docling.ps1";
    private const string VirtualEnvFolderName = "venv";
    private const string VirtualEnvScriptsFolderName = "Scripts";
    private const string DoclingServeFileName = "docling-serve.exe";
    private const string PowerShellCommand = "pwsh";
    private const string PowerShellScriptArguments = "-NoProfile -ExecutionPolicy Bypass -File";
    private const string DoclingServeArguments = "run --host 127.0.0.1 --port 5001 --enable-ui";
    private const string TesseractFolderName = "Tesseract-OCR";
    private const string TesseractFileName = "tesseract.exe";
    private const string TessdataFolderName = "tessdata";
}
