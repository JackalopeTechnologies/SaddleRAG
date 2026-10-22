// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OllamaSharp;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Subjects;

namespace SaddleRAG.ClassifierComparison;

internal static class ComparisonRunner
{
    internal static async Task RunAsync(string output, string model, IReadOnlyList<SubjectDescriptor> descriptors)
    {
        ArgumentException.ThrowIfNullOrEmpty(output);
        ArgumentException.ThrowIfNullOrEmpty(model);
        ArgumentNullException.ThrowIfNull(descriptors);
        using var http = new HttpClient { BaseAddress = new Uri(Endpoint), Timeout = Timeout.InfiniteTimeSpan };
        using var api = new OllamaApiClient(http);
        var recorded = new RecordedGenerateClient(new OllamaApiClientAdapter(api), http, output, model);
        using var classifier = new OllamaLlmClassifier(Options.Create(new OllamaSettings
        {
            Endpoint = Endpoint, ActiveClassificationModel = model,
            ClassificationModels = [new OllamaModelEntry { Name = model }]
        }), recorded, NullLogger<OllamaLlmClassifier>.Instance);
        var repository = Substitute.For<ISubjectCatalogRepository>();
        var assignments = Substitute.For<ISubjectAssignmentRepository>();
        var builder = new SubjectCatalogBuilder(classifier, new GuidSubjectIdGenerator(), TimeProvider.System);
        SubjectCatalogRecord? previous = null;
        var results = new List<ComparisonResult>();
        foreach (var descriptor in descriptors)
        {
            Console.WriteLine($"START {results.Count + 1}/{descriptors.Count} {model}: {descriptor.RelativePath}");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(DocumentTimeoutMinutes));
            var elapsed = Stopwatch.StartNew();
            int callsBefore = recorded.Calls;
            recorded.ResetPlacement();
            ComparisonResult row;
            try
            {
                repository.GetLatestAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(previous);
                var reconciliation = await builder.ReconcileDocumentsAsync(repository,
                    LibraryId, model, [descriptor], deadline.Token);
                var assignment = await reconciliation.PersistAssignmentAsync(assignments,
                    descriptor, ScanId, model, deadline.Token);
                previous = reconciliation.Catalog;
                string primary = previous.Concepts.Single(item => item.Id == assignment.Primary.SubjectId).Label;
                string[] secondary = assignment.Secondary.Select(selection =>
                    previous.Concepts.Single(item => item.Id == selection.SubjectId).Label).ToArray();
                row = new(descriptor.RelativePath, true, primary, secondary, string.Empty,
                    elapsed.Elapsed.TotalSeconds, recorded.Calls - callsBefore, recorded.PeakGpuBytes, recorded.PlacementObserved);
            }
            catch (Exception exception)
            {
                row = new(descriptor.RelativePath, false, string.Empty, [], exception.ToString(),
                    elapsed.Elapsed.TotalSeconds, recorded.Calls - callsBefore, recorded.PeakGpuBytes, recorded.PlacementObserved);
                if (exception is not InvalidDataException and not SubjectClassificationException)
                {
                    results.Add(row);
                    await SaveResultsAsync(output, results, previous);
                    throw;
                }
            }
            results.Add(row);
            await SaveResultsAsync(output, results, previous);
            Console.WriteLine($"RESULT {results.Count}/{descriptors.Count}: valid={row.PassedValidation}, {row.Seconds:F2}s, {row.Primary} {row.Error.Split('\n')[0]}");
        }
        Console.WriteLine($"COMPLETE {results.Count(row => row.PassedValidation)}/{results.Count} structurally valid; semantic review still required.");
    }

    private static async Task SaveResultsAsync(string output, List<ComparisonResult> results, SubjectCatalogRecord? catalog)
    {
        await File.WriteAllTextAsync(Path.Combine(output, ResultsFileName), JsonSerializer.Serialize(results, Program.JsonOptions));
        await File.WriteAllTextAsync(Path.Combine(output, CatalogFileName), JsonSerializer.Serialize(catalog, Program.JsonOptions));
    }

    private const string Endpoint = "http://localhost:11434";
    private const int DocumentTimeoutMinutes = 5;
    private const string LibraryId = "isolated-comparison";
    private const string ScanId = "saved-corpus";
    private const string ResultsFileName = "results.json";
    private const string CatalogFileName = "catalog.json";
}
