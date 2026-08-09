// PackageWxsIceValidationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Xml.Linq;

#endregion

namespace SaddleRAG.Tests.Installer;

/// <summary>
///     Pins the WiX source contracts that keep the generated MSI clear of the
///     ICE03, ICE34, ICE38, ICE43, ICE57, ICE60, and ICE61 findings caught by
///     release validation. These tests complement <c>wix msi validate</c> in CI:
///     they identify the source-level regression instead of reporting only the
///     compiled MSI table that became invalid.
/// </summary>
public sealed class PackageWxsIceValidationTests
{
    [Fact]
    public void JScriptCustomActionsUseBinaryPayloadsAndNamedEntryPoint()
    {
        XDocument package = LoadPackageWxs();
        XNamespace ns = smWixNamespace;

        Assert.DoesNotContain(package.Descendants(ns + CustomActionElementName),
                              action => action.Attribute(ScriptSourceFileAttributeName) != null);

        foreach ((string actionId, string scriptFileName) in smJScriptCustomActions)
        {
            XElement action = package.Descendants(ns + CustomActionElementName)
                                     .Single(element => (string?)element.Attribute(IdAttributeName) == actionId);

            Assert.Null(action.Attribute(ScriptAttributeName));
            Assert.Null(action.Attribute(ScriptSourceFileAttributeName));
            Assert.Equal(JScriptEntryPoint, (string?)action.Attribute(JScriptCallAttributeName));

            string? binaryRef = (string?)action.Attribute(BinaryRefAttributeName);
            Assert.False(string.IsNullOrWhiteSpace(binaryRef), $"{actionId} must reference its JScript Binary row.");

            XElement binary = package.Descendants(ns + BinaryElementName)
                                     .Single(element => (string?)element.Attribute(IdAttributeName) == binaryRef);
            string? sourceFile = (string?)binary.Attribute(SourceFileAttributeName);
            Assert.NotNull(sourceFile);
            Assert.EndsWith(scriptFileName, sourceFile, StringComparison.Ordinal);

            string? scriptPath = InstallerSourceTreeResolver.TryResolveInstallerFile(scriptFileName);
            Assert.NotNull(scriptPath);
            string script = File.ReadAllText(scriptPath);
            Assert.Contains($"function {JScriptEntryPoint}()", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CustomActionTargetsFitMsiSchemaLimit()
    {
        XDocument package = LoadPackageWxs();
        XNamespace ns = smWixNamespace;

        foreach (XElement setProperty in package.Descendants(ns + SetPropertyElementName))
        {
            string id = (string?)setProperty.Attribute(IdAttributeName) ?? MissingIdentifier;
            AssertAttributeFitsMsiTarget(setProperty, ValueAttributeName, id);
        }

        foreach (XElement customAction in package.Descendants(ns + CustomActionElementName))
        {
            string id = (string?)customAction.Attribute(IdAttributeName) ?? MissingIdentifier;
            foreach (string targetAttribute in smCustomActionTargetAttributes)
                AssertAttributeFitsMsiTarget(customAction, targetAttribute, id);
        }
    }

    [Fact]
    public void PatchAppSettingsFormatsEscapedValuesInDeterministicOrder()
    {
        XDocument package = LoadPackageWxs();
        XNamespace ns = smWixNamespace;

        string precedingAction = EscapeAppSettingsActionId;
        foreach (string aliasAction in smPatchAliasActions)
        {
            XElement alias = package.Descendants(ns + SetPropertyElementName)
                                    .Single(element =>
                                                (string?)element.Attribute(ActionAttributeName) == aliasAction);
            Assert.Equal(precedingAction, (string?)alias.Attribute(AfterAttributeName));
            precedingAction = aliasAction;
        }

        XElement setPatch = package.Descendants(ns + SetPropertyElementName)
                                   .Single(element =>
                                               (string?)element.Attribute(IdAttributeName)
                                               == PatchAppSettingsActionId);
        Assert.Equal(SetPatchAppSettingsActionId, (string?)setPatch.Attribute(ActionAttributeName));
        Assert.Equal(precedingAction, (string?)setPatch.Attribute(AfterAttributeName));

        XElement executeSequence = package.Descendants(ns + InstallExecuteSequenceElementName).Single();
        XElement escape = executeSequence.Elements(ns + CustomElementName)
                                         .Single(element =>
                                                     (string?)element.Attribute(ActionAttributeName)
                                                     == EscapeAppSettingsActionId);
        Assert.Equal(InstallFilesActionId, (string?)escape.Attribute(AfterAttributeName));

        XElement patch = executeSequence.Elements(ns + CustomElementName)
                                        .Single(element =>
                                                    (string?)element.Attribute(ActionAttributeName)
                                                    == PatchAppSettingsActionId);
        Assert.Equal(SetPatchAppSettingsActionId, (string?)patch.Attribute(AfterAttributeName));
    }

    [Fact]
    public void MajorUpgradeDoesNotIncludeTheInstalledVersion()
    {
        XDocument package = LoadPackageWxs();
        XNamespace ns = smWixNamespace;
        XElement majorUpgrade = package.Descendants(ns + MajorUpgradeElementName).Single();

        Assert.Null(majorUpgrade.Attribute(AllowSameVersionUpgradesAttributeName));
    }

    [Fact]
    public void OnnxAutoDetectionSentinelIsAValidRadioButtonValue()
    {
        XDocument package = LoadPackageWxs();
        XNamespace ns = smWixNamespace;
        XElement property = package.Descendants(ns + PropertyElementName)
                                   .Single(element =>
                                               (string?)element.Attribute(IdAttributeName)
                                               == OnnxExecutionProviderPropertyId);
        string? defaultValue = (string?)property.Attribute(ValueAttributeName);
        Assert.Equal(AutoExecutionProviderValue, defaultValue);

        XElement group = package.Descendants(ns + RadioButtonGroupElementName)
                                .Single(element =>
                                            (string?)element.Attribute(PropertyAttributeName)
                                            == OnnxExecutionProviderPropertyId);
        Assert.Contains(group.Elements(ns + RadioButtonElementName),
                        radio => (string?)radio.Attribute(ValueAttributeName) == defaultValue);
    }

    [Fact]
    public void TrayExecutableOwnsAdvertisedProgramMenuShortcut()
    {
        XDocument package = LoadPackageWxs();
        XNamespace ns = smWixNamespace;
        XElement publishFiles = PublishFiles(package, ns);

        Assert.Contains(publishFiles.Elements(ns + ExcludeElementName),
                        exclude => (string?)exclude.Attribute(FilesAttributeName) == TrayExecutableSource);

        XElement component = package.Descendants(ns + ComponentElementName)
                                    .Single(element =>
                                                string.Equals(NormalizeGuid((string?)element.Attribute(GuidAttributeName)),
                                                              TrayExecutableComponentGuid,
                                                              StringComparison.OrdinalIgnoreCase));
        XElement executable = component.Elements(ns + FileElementName)
                                       .Single(element =>
                                                   (string?)element.Attribute(SourceAttributeName)
                                                   == TrayExecutableSource);
        Assert.Equal(YesValue, (string?)executable.Attribute(KeyPathAttributeName));

        XElement shortcut = executable.Elements(ns + ShortcutElementName)
                                      .Single(element =>
                                                  (string?)element.Attribute(IdAttributeName)
                                                  == TrayStartMenuShortcutId);
        Assert.Equal(YesValue, (string?)shortcut.Attribute(AdvertiseAttributeName));
        Assert.Equal(TrayMenuDirectoryId, (string?)shortcut.Attribute(DirectoryAttributeName));
        Assert.Null(shortcut.Attribute(TargetAttributeName));

        XElement menuDirectory = package.Descendants(ns + DirectoryElementName)
                                        .Single(element =>
                                                    (string?)element.Attribute(IdAttributeName)
                                                    == TrayMenuDirectoryId);
        XElement? commonPrograms = menuDirectory.Parent;
        Assert.NotNull(commonPrograms);
        Assert.Equal(ns + StandardDirectoryElementName, commonPrograms.Name);
        Assert.Equal(ProgramMenuFolderId, (string?)commonPrograms.Attribute(IdAttributeName));

        string? componentId = (string?)component.Attribute(IdAttributeName);
        Assert.False(string.IsNullOrWhiteSpace(componentId));
        XElement mainFeature = package.Descendants(ns + FeatureElementName)
                                      .Single(element =>
                                                  (string?)element.Attribute(IdAttributeName) == MainFeatureId);
        Assert.Contains(mainFeature.Elements(ns + ComponentRefElementName),
                        componentRef => (string?)componentRef.Attribute(IdAttributeName) == componentId);
    }

    [Fact]
    public void VersionedCodiconFontsAreExcludedAndAuthoredLanguageNeutral()
    {
        XDocument package = LoadPackageWxs();
        XNamespace ns = smWixNamespace;
        XElement publishFiles = PublishFiles(package, ns);

        foreach (string source in smLanguageNeutralCodiconSources)
        {
            _ = Assert.Single(publishFiles.Elements(ns + ExcludeElementName),
                              exclude => (string?)exclude.Attribute(FilesAttributeName) == source);

            XElement file = package.Descendants(ns + FileElementName)
                                   .Single(element => (string?)element.Attribute(SourceAttributeName) == source);
            Assert.Equal(LanguageNeutralValue, (string?)file.Attribute(DefaultLanguageAttributeName));
        }
    }

    [Fact]
    public void BuildWorkflowValidatesMsiAfterBuildingIt()
    {
        string? repositoryRoot = InstallerSourceTreeResolver.TryResolveRepositoryRoot();
        if (repositoryRoot == null)
            Assert.Skip(RepositoryMissingSkipReason);

        string workflowPath = Path.Combine(repositoryRoot,
                                           GitHubDirectoryName,
                                           WorkflowsDirectoryName,
                                           BuildWorkflowFileName);
        string workflow = File.ReadAllText(workflowPath);
        int buildIndex = workflow.IndexOf(MsiBuildCommand, StringComparison.Ordinal);
        int validateIndex = workflow.IndexOf(MsiValidateCommand, StringComparison.Ordinal);

        Assert.True(buildIndex >= 0, "The build workflow must build Package.wxs into the release MSI.");
        Assert.True(validateIndex > buildIndex, "The build workflow must run 'wix msi validate' after building the MSI.");
        Assert.Contains(WixFontWarningExclusion, workflow, StringComparison.Ordinal);
        Assert.Contains(WixWarningsAsErrors, workflow, StringComparison.Ordinal);
    }

    private static void AssertAttributeFitsMsiTarget(XElement element, string attributeName, string actionId)
    {
        string? value = (string?)element.Attribute(attributeName);
        if (value != null)
        {
            Assert.True(value.Length <= MsiCustomActionTargetMaximumLength,
                        $"{actionId} {attributeName} is {value.Length} characters; MSI CustomAction.Target allows "
                        + $"at most {MsiCustomActionTargetMaximumLength}.");
        }
    }

    private static XElement PublishFiles(XDocument package, XNamespace ns)
    {
        XElement publishOutput = package.Descendants(ns + ComponentGroupElementName)
                                        .Single(element =>
                                                    (string?)element.Attribute(IdAttributeName)
                                                    == PublishOutputComponentGroupId);
        return publishOutput.Elements(ns + FilesElementName).Single();
    }

    private static string NormalizeGuid(string? value)
    {
        return value?.Trim().Trim(GuidOpenBrace, GuidCloseBrace) ?? string.Empty;
    }

    private static XDocument LoadPackageWxs()
    {
        string? path = InstallerSourceTreeResolver.TryResolveInstallerFile(WxsFileName);
        if (path == null)
            Assert.Skip(PackageMissingSkipReason);
        Assert.NotNull(path);
        return XDocument.Load(path);
    }

    private static readonly IReadOnlyDictionary<string, string> smJScriptCustomActions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TestMongoConnection"] = "TestMongoConnection.js",
            ["TestOllamaConnection"] = "TestOllamaConnection.js",
            ["LaunchMongoDownload"] = "LaunchMongoDownload.js",
            ["LaunchOllamaDownload"] = "LaunchOllamaDownload.js",
            ["TestDoclingConnection"] = "TestDoclingConnection.js",
            ["OpenDoclingInstallInstructions"] = "OpenDoclingInstallInstructions.js",
            ["OpenDoclingReleases"] = "OpenDoclingReleases.js",
            ["OpenDoclingApiDocumentation"] = "OpenDoclingApiDocumentation.js",
            ["OpenTesseractInstallInstructions"] = "OpenTesseractInstallInstructions.js",
            ["CheckOllamaKeepAlive"] = "CheckOllamaKeepAlive.js",
            ["CheckGpuCapability"] = "CheckGpuCapability.js",
            ["EscapeAppSettingsProperties"] = "EscapeAppSettingsProperties.js"
        };

    private static readonly string[] smCustomActionTargetAttributes =
    [
        DllEntryAttributeName,
        ExeCommandAttributeName,
        JScriptCallAttributeName,
        VBScriptCallAttributeName
    ];

    private static readonly string[] smLanguageNeutralCodiconSources =
    [
        @"$(var.PublishDir)\.playwright\package\lib\vite\dashboard\assets\codicon-DCmgc-ay.ttf",
        @"$(var.PublishDir)\.playwright\package\lib\vite\recorder\assets\codicon-DCmgc-ay.ttf",
        @"$(var.PublishDir)\.playwright\package\lib\vite\traceViewer\codicon.DCmgc-ay.ttf"
    ];

    private static readonly string[] smPatchAliasActions =
    [
        "SetMongoConnectionAlias",
        "SetMongoDatabaseAlias",
        "SetOllamaEndpointAlias",
        "SetDoclingEndpointAlias",
        "SetOnnxProviderAlias",
        "SetEscapeFailureAlias"
    ];

    private static readonly XNamespace smWixNamespace = "http://wixtoolset.org/schemas/v4/wxs";

    private const int MsiCustomActionTargetMaximumLength = 255;

    private const char GuidOpenBrace = '{';
    private const char GuidCloseBrace = '}';

    private const string CustomActionElementName = "CustomAction";
    private const string SetPropertyElementName = "SetProperty";
    private const string MajorUpgradeElementName = "MajorUpgrade";
    private const string PropertyElementName = "Property";
    private const string RadioButtonGroupElementName = "RadioButtonGroup";
    private const string RadioButtonElementName = "RadioButton";
    private const string ComponentGroupElementName = "ComponentGroup";
    private const string FilesElementName = "Files";
    private const string ExcludeElementName = "Exclude";
    private const string ComponentElementName = "Component";
    private const string FileElementName = "File";
    private const string ShortcutElementName = "Shortcut";
    private const string DirectoryElementName = "Directory";
    private const string StandardDirectoryElementName = "StandardDirectory";
    private const string FeatureElementName = "Feature";
    private const string ComponentRefElementName = "ComponentRef";
    private const string BinaryElementName = "Binary";
    private const string InstallExecuteSequenceElementName = "InstallExecuteSequence";
    private const string CustomElementName = "Custom";

    private const string IdAttributeName = "Id";
    private const string ScriptAttributeName = "Script";
    private const string ScriptSourceFileAttributeName = "ScriptSourceFile";
    private const string BinaryRefAttributeName = "BinaryRef";
    private const string JScriptCallAttributeName = "JScriptCall";
    private const string VBScriptCallAttributeName = "VBScriptCall";
    private const string DllEntryAttributeName = "DllEntry";
    private const string ExeCommandAttributeName = "ExeCommand";
    private const string SourceFileAttributeName = "SourceFile";
    private const string SourceAttributeName = "Source";
    private const string ValueAttributeName = "Value";
    private const string AllowSameVersionUpgradesAttributeName = "AllowSameVersionUpgrades";
    private const string PropertyAttributeName = "Property";
    private const string GuidAttributeName = "Guid";
    private const string FilesAttributeName = "Files";
    private const string KeyPathAttributeName = "KeyPath";
    private const string AdvertiseAttributeName = "Advertise";
    private const string DirectoryAttributeName = "Directory";
    private const string TargetAttributeName = "Target";
    private const string DefaultLanguageAttributeName = "DefaultLanguage";
    private const string ActionAttributeName = "Action";
    private const string AfterAttributeName = "After";

    private const string JScriptEntryPoint = "SaddleRagInstallerAction";
    private const string OnnxExecutionProviderPropertyId = "ONNX_EXECUTION_PROVIDER";
    private const string AutoExecutionProviderValue = "Auto";
    private const string TrayExecutableComponentGuid = "3F03A7C2-9BA4-5E60-B1DD-9F301B8D9104";
    private const string TrayExecutableSource = @"$(var.PublishDir)\SaddleRAG.Tray.exe";
    private const string TrayStartMenuShortcutId = "TrayStartMenuShortcut";
    private const string TrayMenuDirectoryId = "SaddleRagMenuDir";
    private const string ProgramMenuFolderId = "ProgramMenuFolder";
    private const string PublishOutputComponentGroupId = "PublishOutput";
    private const string MainFeatureId = "Main";
    private const string YesValue = "yes";
    private const string LanguageNeutralValue = "0";
    private const string MissingIdentifier = "<missing Id>";
    private const string EscapeAppSettingsActionId = "EscapeAppSettingsProperties";
    private const string PatchAppSettingsActionId = "PatchAppSettings";
    private const string SetPatchAppSettingsActionId = "SetPatchAppSettings";
    private const string InstallFilesActionId = "InstallFiles";

    private const string WxsFileName = "Package.wxs";
    private const string GitHubDirectoryName = ".github";
    private const string WorkflowsDirectoryName = "workflows";
    private const string BuildWorkflowFileName = "build.yml";
    private const string MsiBuildCommand = "wix build SaddleRAG.Installer/Package.wxs";
    private const string MsiValidateCommand = "wix msi validate";
    private const string WixFontWarningExclusion = "-sw1101";
    private const string WixWarningsAsErrors = "-wx";

    private const string PackageMissingSkipReason =
        "Package.wxs not locatable from test binary directory; the test requires the WiX source tree.";
    private const string RepositoryMissingSkipReason =
        "Repository root not locatable from test binary directory; the test requires the build workflow source.";
}
