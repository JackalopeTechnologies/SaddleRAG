// OnnxClassifierGenerator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Text;
using Microsoft.ML.OnnxRuntimeGenAI;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Real <see cref="IClassifierGenerator" /> over
///     <c>Microsoft.ML.OnnxRuntimeGenAI</c> 0.14.0. Loads a GenAI model folder
///     (e.g. a phi-4-mini-instruct ONNX variant) and runs the greedy generate
///     loop, applying the generation parameters from the supplied
///     <see cref="ClassifierModelEntry" /> (max output tokens, temperature, stop
///     token). Owns the native <see cref="Model" /> and <see cref="Tokenizer" />
///     and disposes them.
///     This class is intentionally thin and is not unit-tested — it requires a
///     real multi-gigabyte model and the native runtime. All decision logic
///     lives in <see cref="OnnxLlmClassifier" />, which is testable through the
///     <see cref="IClassifierGenerator" /> seam.
/// </summary>
public sealed class OnnxClassifierGenerator : IClassifierGenerator, IDisposable
{
    /// <summary>
    ///     Loads the GenAI model from <paramref name="modelFolder" /> and binds
    ///     the generation parameters from <paramref name="entry" />. The folder
    ///     must contain a complete GenAI model (genai_config.json, the ONNX
    ///     weights, and the tokenizer files) as laid down by
    ///     <c>OnnxModelDownloader.DownloadModelFolderAsync</c>.
    /// </summary>
    public OnnxClassifierGenerator(string modelFolder, ClassifierModelEntry entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelFolder);
        ArgumentNullException.ThrowIfNull(entry);

        if (!Directory.Exists(modelFolder))
            throw new DirectoryNotFoundException(string.Format(MissingModelFolderFormat, modelFolder));

        mEntry = entry;
        mModel = new Model(modelFolder);
        mTokenizer = new Tokenizer(mModel);
    }

    private readonly ClassifierModelEntry mEntry;
    private readonly Model mModel;
    private readonly Tokenizer mTokenizer;
    private bool mDisposed;

    /// <summary>
    ///     Wraps <paramref name="prompt" /> in the phi-4-mini chat template,
    ///     runs the generate loop, and returns the decoded completion text up to
    ///     the configured stop token. Runs on a background thread because the
    ///     GenAI loop is synchronous and CPU-bound.
    /// </summary>
    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(prompt);
        ObjectDisposedException.ThrowIf(mDisposed, this);

        return Task.Run(() => Generate(prompt, ct), ct);
    }

    private string Generate(string prompt, CancellationToken ct)
    {
        string templated = BuildChatPrompt(prompt);

        using var sequences = mTokenizer.Encode(templated);
        int promptTokens = sequences[FirstSequence].Length;
        int maxLength = Math.Min(promptTokens + mEntry.MaxOutputTokens, mEntry.MaxContextLength);

        using var generatorParams = new GeneratorParams(mModel);
        generatorParams.SetSearchOption(MaxLengthOption, maxLength);
        generatorParams.SetSearchOption(TemperatureOption, mEntry.Temperature);
        generatorParams.SetSearchOption(DoSampleOption, mEntry.Temperature > 0f);

        using var generator = new Generator(mModel, generatorParams);
        generator.AppendTokenSequences(sequences);

        using var tokenizerStream = mTokenizer.CreateStream();
        var builder = new StringBuilder();
        int generated = 0;

        while (!generator.IsDone() && generated < mEntry.MaxOutputTokens && !ct.IsCancellationRequested)
        {
            generator.GenerateNextToken();

            int lastToken = generator.GetSequence(FirstSequence)[^1];
            builder.Append(tokenizerStream.Decode(lastToken));
            generated++;

            int stopIndex = builder.ToString().IndexOf(mEntry.Stop, StringComparison.Ordinal);
            if (stopIndex >= 0)
            {
                builder.Length = stopIndex;
                break;
            }
        }

        ct.ThrowIfCancellationRequested();

        return builder.ToString();
    }

    private static string BuildChatPrompt(string userPrompt) =>
        $"{SystemTurnOpen}{ClassifierSystemMessage}{TurnClose}{UserTurnOpen}{userPrompt}{TurnClose}{AssistantTurnOpen}";

    public void Dispose()
    {
        if (!mDisposed)
        {
            mDisposed = true;
            mTokenizer.Dispose();
            mModel.Dispose();
        }
    }

    private const string MissingModelFolderFormat =
        "GenAI classifier model folder '{0}' does not exist. Download the model before constructing the generator.";

    private const ulong FirstSequence = 0UL;

    private const string MaxLengthOption = "max_length";
    private const string TemperatureOption = "temperature";
    private const string DoSampleOption = "do_sample";

    private const string ClassifierSystemMessage = "You are a documentation classifier. Respond with ONLY the requested JSON object.";

    private const string SystemTurnOpen = "<|system|>";
    private const string UserTurnOpen = "<|user|>";
    private const string AssistantTurnOpen = "<|assistant|>";
    private const string TurnClose = "<|end|>";
}
