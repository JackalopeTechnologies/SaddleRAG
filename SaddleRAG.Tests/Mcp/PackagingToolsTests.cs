// PackagingToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using SaddleRAG.Mcp.Tools;
using SaddleRAG.Packaging;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class PackagingToolsTests
{
    [Fact]
    public void PackagingToolsClassHasMcpServerToolTypeAttribute()
    {
        var attr = typeof(PackagingTools).GetCustomAttribute<McpServerToolTypeAttribute>();
        Assert.NotNull(attr);
    }

    [Fact]
    public void ExportLibraryIsDiscoverableAsMcpTool()
    {
        var method = typeof(PackagingTools).GetMethod(nameof(PackagingTools.ExportLibrary));
        Assert.NotNull(method);
        var attr = method.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("export_library", attr.Name);
    }

    [Fact]
    public void ImportLibraryIsDiscoverableAsMcpTool()
    {
        var method = typeof(PackagingTools).GetMethod(nameof(PackagingTools.ImportLibrary));
        Assert.NotNull(method);
        var attr = method.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("import_library", attr.Name);
    }

    [Fact]
    public void ExportLibraryHasDescriptionOnEveryUserFacingParameter()
    {
        var method = typeof(PackagingTools).GetMethod(nameof(PackagingTools.ExportLibrary));
        Assert.NotNull(method);
        var parameters = method.GetParameters();
        foreach (var p in parameters.Where(p => p.ParameterType != typeof(LibraryExporter) &&
                                                p.ParameterType != typeof(CancellationToken)))
        {
            var desc = p.GetCustomAttribute<DescriptionAttribute>();
            Assert.NotNull(desc);
            Assert.False(string.IsNullOrEmpty(desc.Description), $"Parameter {p.Name} has empty description");
        }
    }
}
