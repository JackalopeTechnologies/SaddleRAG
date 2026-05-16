// InspectReportRenderer.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json;

#endregion

namespace SaddleRAG.Cli.Handlers;

/// <summary>
///     Renders the JSON output of the <c>saddlerag-cli inspect</c> in-page
///     evaluator. The Playwright load + JS evaluation stay in Program.cs
///     (they need a real browser); the render half is here so the
///     "Title:", "Total links:", "Collapsible markers:", "Sidebar
///     candidates with &gt;5 links:", and "Links by host:" sections are
///     unit-tested against deterministic JSON. Site-engineering scripts
///     grep these labels.
/// </summary>
public static class InspectReportRenderer
{
    /// <summary>
    ///     Write the inspect report to <paramref name="output" /> based on
    ///     the JSON document produced by the in-page evaluator. Returns 0
    ///     always — render failures should bubble as
    ///     <see cref="JsonException" /> rather than returning a nonzero code.
    /// </summary>
    public static int Render(JsonElement root, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine($"Title: {root.GetProperty(TitlePropertyName).GetString()}");
        output.WriteLine($"Total links: {root.GetProperty(TotalLinksPropertyName).GetInt32()}");
        output.WriteLine();

        output.WriteLine("Collapsible markers:");
        var collapsed = root.GetProperty(CollapsedPropertyName);
        foreach(var prop in collapsed.EnumerateObject())
            output.WriteLine($"  {prop.Name}: {prop.Value.GetInt32()}");
        output.WriteLine();

        output.WriteLine("Sidebar candidates with >5 links:");
        foreach(var sb in root.GetProperty(SidebarsPropertyName).EnumerateArray())
        {
            var id = sb.GetProperty(IdPropertyName).GetString();
            var cls = sb.GetProperty(ClassNamePropertyName).GetString();
            output.WriteLine($"  [{sb.GetProperty(LinkCountPropertyName).GetInt32()} links] {sb.GetProperty(TagPropertyName).GetString()}" +
                             (string.IsNullOrEmpty(id) ? string.Empty : $"#{id}") +
                             (string.IsNullOrEmpty(cls) ? string.Empty : $" .{cls.Replace(" ", ".")}")
                            );
            output.WriteLine($"    selector hint: {sb.GetProperty(SelPropertyName).GetString()}");
            foreach(var sample in sb.GetProperty(SamplesPropertyName).EnumerateArray())
                output.WriteLine($"    sample: {sample.GetString()}");
        }

        output.WriteLine();
        output.WriteLine("Links by host:");
        foreach(var prop in root.GetProperty(LinksByHostPropertyName).EnumerateObject())
            output.WriteLine($"  {prop.Name}: {prop.Value.GetInt32()}");

        return 0;
    }

    private const string TitlePropertyName = "title";
    private const string TotalLinksPropertyName = "totalLinks";
    private const string CollapsedPropertyName = "collapsed";
    private const string SidebarsPropertyName = "sidebars";
    private const string LinkCountPropertyName = "linkCount";
    private const string TagPropertyName = "tag";
    private const string IdPropertyName = "id";
    private const string ClassNamePropertyName = "className";
    private const string SelPropertyName = "sel";
    private const string SamplesPropertyName = "samples";
    private const string LinksByHostPropertyName = "linksByHost";
}
