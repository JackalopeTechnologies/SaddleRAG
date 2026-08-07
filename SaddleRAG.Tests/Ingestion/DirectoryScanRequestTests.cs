// DirectoryScanRequestTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Ingestion.Scanning;

namespace SaddleRAG.Tests.Ingestion;

public sealed class DirectoryScanRequestTests
{
    [Fact]
    public void PreviewRequestContainsNoDateOrVersionReservation()
    {
        var properties = typeof(DirectoryScanRequest).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("Version", properties);
        Assert.DoesNotContain("Date", properties);
        Assert.DoesNotContain("ScanDate", properties);
        Assert.Contains("ScanRunId", properties);
    }

    [Fact]
    public void PreviewIsNonRecursiveByDefaultAndHasAnExplicitBound()
    {
        var request = new DirectoryScanRequest
                          {
                              LibraryId = "manual-library",
                              ScanRunId = "scan-run",
                              RootPath = "C:\\manuals"
                          };

        Assert.False(request.Recursive);
        Assert.True(request.MaxFileBytes > 0);
    }

    [Fact]
    public void ScannerHasNoAutomaticHostWatcherPublicationOrIndexDependency()
    {
        var scannerType = typeof(DirectoryScanner);
        var interfaces = scannerType.GetInterfaces().Select(i => i.FullName).ToArray();
        var constructor = Assert.Single(scannerType.GetConstructors());
        var dependencies = constructor.GetParameters().Select(p => p.ParameterType.Name).ToArray();
        var fields = scannerType.GetFields(System.Reflection.BindingFlags.Instance
                                           | System.Reflection.BindingFlags.NonPublic);

        Assert.DoesNotContain("Microsoft.Extensions.Hosting.IHostedService", interfaces);
        Assert.DoesNotContain("ILibraryRepository", dependencies);
        Assert.DoesNotContain("IVectorSearchProvider", dependencies);
        Assert.DoesNotContain("IPageRepository", dependencies);
        Assert.DoesNotContain("IChunkRepository", dependencies);
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(FileSystemWatcher)
                                               || field.FieldType == typeof(Timer));
    }
}
