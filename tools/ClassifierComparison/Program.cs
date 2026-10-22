// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text.Json;
using SaddleRAG.Ingestion.Subjects;

namespace SaddleRAG.ClassifierComparison;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length is < 3 or > 4)
            throw new ArgumentException("Use <descriptors.json> <new-output-directory> <model> [document-count].");
        string input = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output))
            throw new IOException("Use a new output directory so previous evidence cannot be overwritten.");
        byte[] bytes = await File.ReadAllBytesAsync(input);
        var descriptors = JsonSerializer.Deserialize<List<SubjectDescriptor>>(bytes)
                          ?? throw new InvalidDataException("The descriptor file is empty.");
        int count = args.Length == 4 ? int.Parse(args[3]) : descriptors.Count;
        if (count < 1 || count > descriptors.Count)
            throw new ArgumentOutOfRangeException(nameof(args), "Document count must be within the exported corpus.");
        Directory.CreateDirectory(output);
        var selected = descriptors.OrderBy(item => item.DocumentId, StringComparer.Ordinal).Take(count).ToArray();
        await File.WriteAllTextAsync(Path.Combine(output, RunFileName), JsonSerializer.Serialize(new
        {
            model = args[2], input, inputSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            documents = count, promptVersion = SubjectCatalogPrompt.PromptVersion,
            contextTokens = RecordedGenerateClient.ContextTokens,
            outputTokens = RecordedGenerateClient.OutputTokens, temperature = 0, seed = RecordedGenerateClient.Seed,
            cpuThreads = RecordedGenerateClient.CpuThreads, keepAliveSeconds = 0, thinking = false,
            assembly = typeof(Program).Assembly.GetName().Version?.ToString(),
            startedUtc = DateTimeOffset.UtcNow
        }, JsonOptions));
        await ComparisonRunner.RunAsync(output, args[2], selected);
        return 0;
    }

    internal static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };
    private const string RunFileName = "run.json";
}
