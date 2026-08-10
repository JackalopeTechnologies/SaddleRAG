// RegisterExternalToolsCommand.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.CommandLine;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Cli.Commands;

/// <summary>
///     Records where the user's Docling and Tesseract installs live.
///     <para>
///         The installer runs this from an impersonated custom action so the registry
///         lands in the installing user's profile — the MSI's own configuration step runs
///         as SYSTEM, where %LOCALAPPDATA% is the service profile, not the user's.
///     </para>
///     <para>
///         This records paths only. It never downloads, installs, licenses, or configures
///         either product.
///     </para>
/// </summary>
public static class RegisterExternalToolsCommand
{
    private const string CommandName = "register-external-tools";
    private const string CommandDescription =
        "Record the user's Docling and Tesseract locations. Blank values fall back to detection; nothing is installed.";
    private const string DoclingCommandOptionName = "--docling-command";
    private const string DoclingCommandOptionDescription = "Command that starts Docling Serve; blank to auto-detect";
    private const string DoclingArgumentsOptionName = "--docling-arguments";
    private const string DoclingArgumentsOptionDescription = "Arguments passed to the Docling start command";
    private const string TesseractDirectoryOptionName = "--tesseract-directory";
    private const string TesseractDirectoryOptionDescription = "Tesseract program directory; blank to auto-detect";
    private const string TessdataDirectoryOptionName = "--tessdata-directory";
    private const string TessdataDirectoryOptionDescription = "tessdata directory; blank to use the tessdata sibling";
    private const string RegistryPathOptionName = "--registry-path";
    private const string RegistryPathOptionDescription = "Registry file to write; defaults to the per-user location";
    private const string QuietOptionName = "--quiet";
    private const string QuietOptionDescription = "Suppress the summary line";
    private const string SummaryFormat = "external tools: docling={0} tesseract={1} -> {2}";
    private const string NotRegisteredLabel = "(not registered)";
    private const int ExitCodeOk = 0;

    private static readonly Option<string?> smDoclingCommand = new(DoclingCommandOptionName)
                                                               {
                                                                   Description = DoclingCommandOptionDescription
                                                               };
    private static readonly Option<string?> smDoclingArguments = new(DoclingArgumentsOptionName)
                                                                 {
                                                                     Description = DoclingArgumentsOptionDescription
                                                                 };
    private static readonly Option<string?> smTesseractDirectory = new(TesseractDirectoryOptionName)
                                                                   {
                                                                       Description = TesseractDirectoryOptionDescription
                                                                   };
    private static readonly Option<string?> smTessdataDirectory = new(TessdataDirectoryOptionName)
                                                                  {
                                                                      Description = TessdataDirectoryOptionDescription
                                                                  };
    private static readonly Option<string?> smRegistryPath = new(RegistryPathOptionName)
                                                             {
                                                                 Description = RegistryPathOptionDescription
                                                             };
    private static readonly Option<bool> smQuiet = new(QuietOptionName)
                                                   {
                                                       Description = QuietOptionDescription,
                                                       DefaultValueFactory = _ => false
                                                   };

    public static Command Build()
    {
        Command cmd = new(CommandName, CommandDescription);
        cmd.Options.Add(smDoclingCommand);
        cmd.Options.Add(smDoclingArguments);
        cmd.Options.Add(smTesseractDirectory);
        cmd.Options.Add(smTessdataDirectory);
        cmd.Options.Add(smRegistryPath);
        cmd.Options.Add(smQuiet);
        cmd.SetAction(Execute);
        return cmd;
    }

    private static int Execute(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        string registryPath = parseResult.GetValue(smRegistryPath) ?? ExternalToolRegistry.DefaultPath;
        ExternalToolRegistry registry = new(registryPath);
        ExternalToolRegistration resolved = ExternalToolRegistrationResolver.Resolve(
            registry.Read(),
            new ExternalToolDetector(new FileSystemProbe()),
            parseResult.GetValue(smDoclingCommand),
            parseResult.GetValue(smDoclingArguments),
            parseResult.GetValue(smTesseractDirectory),
            parseResult.GetValue(smTessdataDirectory));
        registry.Write(resolved);

        if (!parseResult.GetValue(smQuiet))
        {
            Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                            SummaryFormat,
                                            resolved.Docling?.Command ?? NotRegisteredLabel,
                                            resolved.Tesseract?.ExecutableDirectory ?? NotRegisteredLabel,
                                            registryPath));
        }

        return ExitCodeOk;
    }
}
