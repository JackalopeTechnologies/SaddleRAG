// DirectoryScanStatusDetailTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Scanning;
using SaddleRAG.Monitor.Pages;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

/// <summary>
///     "Running" on its own tells an operator nothing. A directory scan row has to say
///     what it is converting, how far in it is, when it last moved, and why it stopped.
/// </summary>
public sealed class DirectoryScanStatusDetailTests
{
    [Fact]
    public async Task RowCarriesTheJobTimingAndErrorNeededToJudgeARunningScan()
    {
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var sources = Substitute.For<ISourceDocumentRepository>();
        var libraries = Substitute.For<ILibraryRepository>();
        var jobs = Substitute.For<IJobRepository>();
        factory.GetSourceDocumentRepository(null).Returns(sources);
        factory.GetLibraryRepository(null).Returns(libraries);
        factory.GetJobRepository(null).Returns(jobs);
        sources.GetDirectoryDefinitionsAsync(Arg.Any<CancellationToken>()).Returns(new[] { Definition() });
        jobs.ListRecentAsync(JobType.DirectoryScan, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { RunningJob() });
        var sut = new DirectoryLibraryMonitorDataService(factory);

        IReadOnlyList<DirectoryLibraryMonitorRow> rows =
            await sut.ListAsync(profile: null, TestContext.Current.CancellationToken);

        DirectoryLibraryMonitorRow row = Assert.Single(rows);
        Assert.Equal(StartedAt, row.LatestJobStartedAt);
        Assert.Equal(LastProgressAt, row.LatestJobLastProgressAt);
        Assert.Equal("Docling stopped responding", row.LatestJobError);
    }

    [Fact]
    public void DiscoveryLineSeparatesWhatWasFoundFromWhatCanBeConverted()
    {
        string line = DirectoryLibrariesPageBase.FormatDiscovery(new DirectoryScanJobProgress
                                                                     {
                                                                         FilesDiscovered = 120,
                                                                         SupportedDocuments = 42,
                                                                         DocumentsCompleted = 7
                                                                     });

        Assert.Contains("120", line, StringComparison.Ordinal);
        Assert.Contains("42", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TimingLineReportsStartAndLastProgressForARunningScan()
    {
        DirectoryLibraryMonitorRow row = Row() with
            {
                LatestJobStatus = nameof(JobStatus.Running),
                LatestJobStartedAt = DateTime.UtcNow.AddMinutes(-32),
                LatestJobLastProgressAt = DateTime.UtcNow.AddMinutes(-2)
            };

        string line = DirectoryLibrariesPageBase.FormatJobTiming(row);

        Assert.Contains("32", line, StringComparison.Ordinal);
        Assert.Contains("2", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TimingLineSaysSoWhenNoScanHasEverRun()
    {
        string line = DirectoryLibrariesPageBase.FormatJobTiming(Row());

        Assert.Contains("No scan", line, StringComparison.OrdinalIgnoreCase);
    }

    private static DirectoryLibraryMonitorRow Row() => new()
        {
            LibraryId = LibraryId,
            Name = "Service Manuals",
            Hint = "maintenance and setup",
            RootPath = RootPath,
            Recursive = true,
            AllowedExtensions = DirectoryScanLimits.SupportedExtensions,
            FileFailures = []
        };

    private static DirectoryLibraryDefinition Definition() => new()
        {
            Id = LibraryId,
            RootPath = RootPath,
            Recursive = true,
            AllowedExtensions = DirectoryScanLimits.SupportedExtensions,
            ExclusionPatterns = [],
            BindingStatus = DirectoryLibraryBindingStatus.Bound,
            RegisteredAtUtc = DateTime.UtcNow
        };

    private static JobRecord RunningJob() => new()
        {
            Id = "job-11",
            JobType = JobType.DirectoryScan,
            LibraryId = LibraryId,
            Version = "2026-08-10",
            Status = JobStatus.Running,
            CreatedAt = StartedAt.AddSeconds(-1),
            StartedAt = StartedAt,
            LastProgressAt = LastProgressAt,
            ErrorMessage = "Docling stopped responding",
            DirectoryScanProgress = new DirectoryScanJobProgress
                                        {
                                            FilesDiscovered = 120,
                                            SupportedDocuments = 42,
                                            DocumentsCompleted = 7,
                                            CurrentRelativePath = "manuals/hydraulics.pdf"
                                        }
        };

    private static readonly DateTime StartedAt = new(2026, 8, 10, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LastProgressAt = new(2026, 8, 10, 14, 31, 0, DateTimeKind.Utc);

    private const string LibraryId = "service-manuals";
    private const string RootPath = "D:\\User Libraries\\Service Manuals";
}
