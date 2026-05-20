// SkillManifest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.ClientIntegration.Writers;

internal static class SkillManifest
{
    private const string ResourcePrefix    = "SaddleRAG.ClientIntegration.Resources.";
    private const string MdExtension       = ".md";

    internal const string FirstFolderName          = "saddlerag-first";
    internal const string ReconFolderName          = "saddlerag-recon";
    internal const string ScrapeFolderName         = "saddlerag-scrape";
    internal const string ScrapeStrategyFolderName = "saddlerag-scrape-strategy";
    internal const string MaintainFolderName       = "saddlerag-maintain";
    internal const string QueryFolderName          = "saddlerag-query";

    private const string FirstResource          = ResourcePrefix + FirstFolderName          + MdExtension;
    private const string ReconResource          = ResourcePrefix + ReconFolderName          + MdExtension;
    private const string ScrapeResource         = ResourcePrefix + ScrapeFolderName         + MdExtension;
    private const string ScrapeStrategyResource = ResourcePrefix + ScrapeStrategyFolderName + MdExtension;
    private const string MaintainResource       = ResourcePrefix + MaintainFolderName       + MdExtension;
    private const string QueryResource          = ResourcePrefix + QueryFolderName          + MdExtension;

    internal static readonly SkillDescriptor[] pmAll =
        [
            new(FirstResource,          FirstFolderName),
            new(ReconResource,          ReconFolderName),
            new(ScrapeResource,         ScrapeFolderName),
            new(ScrapeStrategyResource, ScrapeStrategyFolderName),
            new(MaintainResource,       MaintainFolderName),
            new(QueryResource,          QueryFolderName),
        ];
}
