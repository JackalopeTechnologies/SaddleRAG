// InspectReportRendererTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json;
using SaddleRAG.Cli.Handlers;

#endregion

namespace SaddleRAG.Tests.Cli;

/// <summary>
///     Pins the saddlerag-cli inspect output sections. Site-engineering
///     scripts grep for "Sidebar candidates with >5 links:", "Links by
///     host:", "Collapsible markers:" — silent renames would break them.
///     Built against constructed JSON since the actual data source is an
///     in-page Playwright evaluator that can't be unit-tested.
/// </summary>
public sealed class InspectReportRendererTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void RenderPrintsTitleAndTotalLinks()
    {
        var root = Parse("""
                         {
                             "title": "Example Page",
                             "totalLinks": 42,
                             "collapsed": {},
                             "sidebars": [],
                             "linksByHost": {}
                         }
                         """
                        );
        var output = new StringWriter();

        InspectReportRenderer.Render(root, output);

        var rendered = output.ToString();
        Assert.Contains("Title: Example Page", rendered);
        Assert.Contains("Total links: 42", rendered);
    }

    [Fact]
    public void RenderPrintsCollapsibleMarkersSection()
    {
        var root = Parse("""
                         {
                             "title": "t",
                             "totalLinks": 0,
                             "collapsed": { "ariaCollapsed": 5, "collapsedClass": 3 },
                             "sidebars": [],
                             "linksByHost": {}
                         }
                         """
                        );
        var output = new StringWriter();

        InspectReportRenderer.Render(root, output);

        var rendered = output.ToString();
        Assert.Contains("Collapsible markers:", rendered);
        Assert.Contains("ariaCollapsed: 5", rendered);
        Assert.Contains("collapsedClass: 3", rendered);
    }

    [Fact]
    public void RenderPrintsSidebarCandidatesWithLinkCountTagIdAndClassName()
    {
        var root = Parse("""
                         {
                             "title": "t",
                             "totalLinks": 0,
                             "collapsed": {},
                             "sidebars": [
                                 {
                                     "sel": "nav",
                                     "tag": "NAV",
                                     "id": "primary-nav",
                                     "className": "nav primary",
                                     "linkCount": 25,
                                     "samples": ["https://a/1", "https://a/2"]
                                 }
                             ],
                             "linksByHost": {}
                         }
                         """
                        );
        var output = new StringWriter();

        InspectReportRenderer.Render(root, output);

        var rendered = output.ToString();
        Assert.Contains("Sidebar candidates with >5 links:", rendered);
        // Format: "[25 links] NAV#primary-nav .nav.primary"
        Assert.Contains("[25 links] NAV#primary-nav .nav.primary", rendered);
        Assert.Contains("selector hint: nav", rendered);
        Assert.Contains("sample: https://a/1", rendered);
        Assert.Contains("sample: https://a/2", rendered);
    }

    [Fact]
    public void RenderOmitsIdSuffixWhenSidebarIdIsEmpty()
    {
        var root = Parse("""
                         {
                             "title": "t",
                             "totalLinks": 0,
                             "collapsed": {},
                             "sidebars": [
                                 { "sel": "aside", "tag": "ASIDE", "id": "", "className": "", "linkCount": 6, "samples": [] }
                             ],
                             "linksByHost": {}
                         }
                         """
                        );
        var output = new StringWriter();

        InspectReportRenderer.Render(root, output);

        var rendered = output.ToString();
        Assert.Contains("[6 links] ASIDE", rendered);
        // No "#" suffix and no " ." classname suffix.
        Assert.DoesNotContain("ASIDE#", rendered);
        Assert.DoesNotContain("ASIDE .", rendered);
    }

    [Fact]
    public void RenderPrintsLinksByHostSection()
    {
        var root = Parse("""
                         {
                             "title": "t",
                             "totalLinks": 0,
                             "collapsed": {},
                             "sidebars": [],
                             "linksByHost": { "docs.example.com": 12, "github.com": 3 }
                         }
                         """
                        );
        var output = new StringWriter();

        InspectReportRenderer.Render(root, output);

        var rendered = output.ToString();
        Assert.Contains("Links by host:", rendered);
        Assert.Contains("docs.example.com: 12", rendered);
        Assert.Contains("github.com: 3", rendered);
    }
}
