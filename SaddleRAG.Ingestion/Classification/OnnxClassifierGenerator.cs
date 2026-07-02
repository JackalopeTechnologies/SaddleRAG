// OnnxClassifierGenerator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text;
using Microsoft.ML.OnnxRuntimeGenAI;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Real <see cref="IClassifierGenerator" /> over
///     <c>Microsoft.ML.OnnxRuntimeGenAI</c> 0.14.0. Loads a GenAI model folder
///     (e.g. a phi-3-mini-4k-instruct ONNX variant) and runs the greedy generate
///     loop, applying the generation parameters from the supplied
///     <see cref="ClassifierModelEntry" /> (max output tokens, temperature, stop
///     token). Owns the native <see cref="Model" /> and <see cref="Tokenizer" />
///     and disposes them.
///     This class is intentionally thin and is not unit-tested — it requires a
///     real multi-gigabyte model and the native runtime. All decision logic
///     lives in <see cref="OnnxLlmClassifier" />, which is testable through the
///     <see cref="IClassifierGenerator" /> seam.
///     NOT thread-safe for concurrent generation: native <see cref="Generator" />
///     creation over the shared <see cref="Model" /> access-violates when calls
///     overlap (issue #135). Composition roots must wrap this class in
///     <see cref="SerializedClassifierGenerator" />.
/// </summary>
public sealed class OnnxClassifierGenerator : IClassifierGenerator, IDisposable
{
    /// <summary>
    ///     Binds <paramref name="modelFolder" /> and the generation parameters
    ///     from <paramref name="entry" /> without touching disk. The native
    ///     GenAI <see cref="Model" /> and <see cref="Tokenizer" /> load lazily
    ///     on the first <see cref="GenerateAsync" /> (or explicit
    ///     <see cref="EnsureLoaded" />) call, so the generator can be
    ///     constructed at DI time before warmup has downloaded the model
    ///     folder. The folder must contain a complete GenAI model
    ///     (genai_config.json, the ONNX weights, and the tokenizer files) as
    ///     laid down by <c>OnnxModelDownloader.DownloadModelFolderAsync</c>
    ///     by the time the first generate runs.
    /// </summary>
    public OnnxClassifierGenerator(string modelFolder, ClassifierModelEntry entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelFolder);
        ArgumentNullException.ThrowIfNull(entry);

        mModelFolder = modelFolder;
        mEntry = entry;
    }

    private readonly string mModelFolder;
    private readonly ClassifierModelEntry mEntry;
    private readonly Lock mLoadLock = new();
    private Model? mModel;
    private Tokenizer? mTokenizer;
    private bool mDisposed;

    /// <inheritdoc />
    public string ModelId => mEntry.Name;

    /// <summary>
    ///     Loads the native GenAI <see cref="Model" /> and
    ///     <see cref="Tokenizer" /> from the configured folder if they are not
    ///     already loaded, returning the (model, tokenizer) pair. Thread-safe:
    ///     concurrent first-callers block on <see cref="mLoadLock" /> so the
    ///     native model is constructed exactly once. Warmup may call this
    ///     explicitly after the download step so the first real classification
    ///     isn't cold; <see cref="GenerateAsync" /> calls it implicitly.
    /// </summary>
    public void EnsureLoaded()
    {
        ObjectDisposedException.ThrowIf(mDisposed, this);
        LoadOrGet();
    }

    private (Model Model, Tokenizer Tokenizer) LoadOrGet()
    {
        Model? model = mModel;
        Tokenizer? tokenizer = mTokenizer;

        if (model == null || tokenizer == null)
            (model, tokenizer) = LoadUnderLock();

        return (model, tokenizer);
    }

    private (Model Model, Tokenizer Tokenizer) LoadUnderLock()
    {
        lock (mLoadLock)
        {
            Model? model = mModel;
            Tokenizer? tokenizer = mTokenizer;

            if (model == null || tokenizer == null)
                (model, tokenizer) = LoadModelAndTokenizer();

            return (model, tokenizer);
        }
    }

    private (Model Model, Tokenizer Tokenizer) LoadModelAndTokenizer()
    {
        if (!Directory.Exists(mModelFolder))
            throw new DirectoryNotFoundException(string.Format(MissingModelFolderFormat, mModelFolder));

        var model = new Model(mModelFolder);
        var tokenizer = new Tokenizer(model);
        mModel = model;
        mTokenizer = tokenizer;
        return (model, tokenizer);
    }

    /// <summary>
    ///     Wraps <paramref name="prompt" /> in the Phi-3-mini-4k chat template,
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
        (Model model, Tokenizer tokenizer) = LoadOrGet();

        string templated = BuildChatPrompt(prompt);

        using var sequences = tokenizer.Encode(templated);
        int promptTokens = sequences[FirstSequence].Length;
        int maxLength = Math.Min(promptTokens + mEntry.MaxOutputTokens, mEntry.MaxContextLength);

        using var generatorParams = new GeneratorParams(model);
        generatorParams.SetSearchOption(MaxLengthOption, maxLength);
        generatorParams.SetSearchOption(TemperatureOption, mEntry.Temperature);
        generatorParams.SetSearchOption(DoSampleOption, mEntry.Temperature > 0f);

        using var generator = new Generator(model, generatorParams);
        generator.AppendTokenSequences(sequences);

        using var tokenizerStream = tokenizer.CreateStream();
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
            mTokenizer?.Dispose();
            mModel?.Dispose();
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
