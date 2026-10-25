// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using SaddleRAG.Core.Models;

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>Recognizes the built-in Phi-3 DirectML configuration covered by the native diagnostic review.</summary>
internal static class OnnxClassifierDiagnosticEligibility
{
    internal static bool IsEligible(ClassifierModelEntry entry, string configuration, string graphSha256,
        long graphLength = ReviewedGraphLength)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(graphSha256);
        bool eligible = false;
        if (entry.Name == OnnxSettings.Phi3MiniDirectMlName &&
            entry.RepoId == OnnxSettings.Phi3MiniRepoId &&
            entry.ModelFolder == OnnxSettings.Phi3MiniDirectMlFolder &&
            graphLength == ReviewedGraphLength &&
            string.Equals(graphSha256, ReviewedGraphSha256, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                eligible = MatchesConfiguration(JsonNode.Parse(configuration));
            }
            catch (JsonException)
            {
                // Native model loading remains responsible for reporting invalid configuration.
            }
            catch (ArgumentException)
            {
                // Duplicate JSON properties cannot qualify a configuration for reclassification.
            }
        }
        return eligible;
    }

    internal static string ReadGraphFingerprint(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelDirectory);
        using FileStream graph = File.OpenRead(Path.Combine(modelDirectory, DecoderFilename));
        string fingerprint = graph.Length == ReviewedGraphLength ? Convert.ToHexString(SHA256.HashData(graph)) : string.Empty;
        return fingerprint;
    }

    internal static bool IsCurrentRuntimeReviewed()
    {
        var versions = ReadCurrentRuntimeVersions();
        return MatchesRuntime(versions.Runtime, versions.GenAi);
    }

    internal static (string? Runtime, string? GenAi) ReadCurrentRuntimeVersions()
    {
        (string? Runtime, string? GenAi) versions = (null, null);
#if USE_GPU && !USE_GPU_CUDA
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using Process process = Process.GetCurrentProcess();
                ProcessModule? genAi = process.Modules.Cast<ProcessModule>().FirstOrDefault(module =>
                    string.Equals(module.ModuleName, GenAiLibraryFilename, StringComparison.OrdinalIgnoreCase));
                versions = (OrtEnv.Instance().GetVersionString(), genAi?.FileVersionInfo.ProductVersion);
            }
            catch (Exception)
            {
                // Version inspection is an optional diagnostic guard, never a new model-load failure.
            }
        }
#endif
        return versions;
    }

    internal static bool MatchesRuntime(string? runtimeVersion, string? genAiVersion) =>
        runtimeVersion == ReviewedRuntimeVersion && genAiVersion == ReviewedGenAiVersion;

    private static bool MatchesConfiguration(JsonNode? configuration)
    {
        bool eligible = configuration is JsonObject root &&
                        root[ModelKey] is JsonObject model &&
                        IsValue(model[TypeKey], Phi3ModelType) &&
                        model[DecoderKey] is JsonObject decoder &&
                        IsValue(decoder[FilenameKey], DecoderFilename) &&
                        !decoder.ContainsKey(SlidingWindowKey) &&
                        decoder[SessionOptionsKey] is JsonObject options &&
                        options.All(option => option.Key is LogIdKey or ProviderOptionsKey) &&
                        options[ProviderOptionsKey] is JsonArray providers &&
                        providers.Count == 1 && IsDirectMlProvider(providers[0]) &&
                        root[SearchKey] is JsonObject search &&
                        IsValue(search[ShareBufferKey], true) &&
                        IsValue(search[NumBeamsKey], 1) &&
                        IsValue(search[NumReturnSequencesKey], 1) &&
                        (!search.ContainsKey(BatchSizeKey) || IsValue(search[BatchSizeKey], 1)) &&
                        !search.ContainsKey(ChunkSizeKey);
        return eligible;
    }

    private static bool IsDirectMlProvider(JsonNode? node)
    {
        bool directMl = node is JsonObject provider && provider.Count == 1 &&
                        provider.Single() is var option &&
                        string.Equals(option.Key, DirectMlProvider, StringComparison.OrdinalIgnoreCase) &&
                        option.Value is JsonObject providerOptions && providerOptions.Count == 0;
        return directMl;
    }

    private static bool IsValue<T>(JsonNode? node, T expected)
    {
        bool matches = node is JsonValue value && value.TryGetValue(out T? actual) &&
                       EqualityComparer<T>.Default.Equals(actual, expected);
        return matches;
    }

    internal const string ConfigurationFilename = "genai_config.json";
    internal const string ModelKey = "model";
    internal const string DecoderKey = "decoder";
    internal const string SessionOptionsKey = "session_options";
    internal const string LogIdKey = "log_id";
    private const string TypeKey = "type";
    private const string Phi3ModelType = "phi3";
    private const string FilenameKey = "filename";
    private const string DecoderFilename = "model.onnx";
    private const string ProviderOptionsKey = "provider_options";
    private const string DirectMlProvider = "dml";
    private const string SearchKey = "search";
    private const string ShareBufferKey = "past_present_share_buffer";
    private const string NumBeamsKey = "num_beams";
    private const string NumReturnSequencesKey = "num_return_sequences";
    private const string BatchSizeKey = "batch_size";
    private const string ChunkSizeKey = "chunk_size";
    private const string SlidingWindowKey = "sliding_window";
    private const string GenAiLibraryFilename = "onnxruntime-genai.dll";
    private const string ReviewedRuntimeVersion = "1.24.4";
    private const string ReviewedGenAiVersion = "0.14.1";
    // Microsoft model metadata at revision 5f5f794c1c23c9d5ee142af85df02a6cc52d6945.
    // Fingerprint only the 2.1 MB graph; external weights are never read or changed here.
    internal const string ReviewedGraphSha256 = "21E1C8CA461E8AFFB8344832C0CAE22C9125CDE31CA8B58E16F4B70CB840D4C6";
    internal const long ReviewedGraphLength = 2109332;
}
