// OnnxClassifierGenerator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text;
using System.Text.Json;
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
///     Native inference requires a real multi-gigabyte model. Prompt framing
///     and token-budget decisions are exposed as internal pure seams so their
///     contracts can be tested without loading that model.
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
        ValidateEntry(entry);

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
        int promptTokenBudget = GetPromptTokenBudget(mEntry);
        string boundedPrompt = FitPromptToTokenBudget(
                                   prompt,
                                   promptTokenBudget,
                                   candidate => CountChatTokens(tokenizer, candidate),
                                   (candidate, tokenCount) => TakePromptTokens(tokenizer,
                                                                                candidate,
                                                                                tokenCount));
        string templated = BuildChatPrompt(boundedPrompt);

        using var sequences = tokenizer.Encode(templated);
        int promptTokens = sequences[FirstSequence].Length;
        int maxLength = promptTokens + mEntry.MaxOutputTokens;

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

            string generatedText = builder.ToString();
            int completeJsonEnd = FindCompleteRootJsonObjectEnd(generatedText);
            if (completeJsonEnd >= 0)
            {
                builder.Length = completeJsonEnd;
                break;
            }

            int stopIndex = generatedText.IndexOf(mEntry.Stop, StringComparison.Ordinal);
            if (stopIndex >= 0)
            {
                builder.Length = stopIndex;
                break;
            }
        }

        ct.ThrowIfCancellationRequested();

        return builder.ToString();
    }

    /// <summary>
    ///     Finds the exclusive end of the first complete root JSON object when
    ///     the response begins with only whitespace before that object. Braces
    ///     inside JSON strings are ignored. A negative result preserves strict
    ///     downstream rejection of prose prefixes and incomplete JSON.
    /// </summary>
    internal static int FindCompleteRootJsonObjectEnd(string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        int result = NoCompleteJsonObjectEnd;
        int index = 0;
        while (index < response.Length && char.IsWhiteSpace(response[index]))
            index++;

        bool rootObjectStarted = index < response.Length && response[index] == JsonObjectOpen;
        if (rootObjectStarted)
        {
            int depth = 0;
            bool insideString = false;
            bool escaped = false;
            while (index < response.Length && result < 0)
            {
                char current = response[index];
                result = ScanJsonCharacter(current,
                                           index,
                                           ref depth,
                                           ref insideString,
                                           ref escaped);
                index++;
            }
        }

        return result;
    }

    private static int ScanJsonCharacter(char current,
                                         int index,
                                         ref int depth,
                                         ref bool insideString,
                                         ref bool escaped)
    {
        int result = NoCompleteJsonObjectEnd;
        if (insideString)
            ScanJsonStringCharacter(current, ref insideString, ref escaped);
        else
            result = ScanJsonStructureCharacter(current, index, ref depth, ref insideString);
        return result;
    }

    private static void ScanJsonStringCharacter(char current,
                                                ref bool insideString,
                                                ref bool escaped)
    {
        if (escaped)
            escaped = false;
        else
        {
            switch (current)
            {
                case JsonEscape:
                    escaped = true;
                    break;
                case JsonQuote:
                    insideString = false;
                    break;
            }
        }
    }

    private static int ScanJsonStructureCharacter(char current,
                                                  int index,
                                                  ref int depth,
                                                  ref bool insideString)
    {
        int result = NoCompleteJsonObjectEnd;
        switch (current)
        {
            case JsonQuote:
                insideString = true;
                break;
            case JsonObjectOpen:
                depth++;
                break;
            case JsonObjectClose:
                depth--;
                result = depth == 0 ? index + 1 : NoCompleteJsonObjectEnd;
                break;
        }

        return result;
    }

    /// <summary>
    ///     Applies the newline-delimited Phi-3 chat template distributed in
    ///     the built-in model's <c>tokenizer_config.json</c>.
    /// </summary>
    internal static string BuildChatPrompt(string userPrompt)
    {
        ArgumentNullException.ThrowIfNull(userPrompt);
        string result =
            $"{SystemTurnOpen}\n{ClassifierSystemMessage}{TurnClose}\n{UserTurnOpen}\n{userPrompt}{TurnClose}\n{AssistantTurnOpen}\n";
        return result;
    }

    /// <summary>
    ///     Returns the input-token allowance remaining after reserving every
    ///     configured output token.
    /// </summary>
    internal static int GetPromptTokenBudget(ClassifierModelEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int result = entry.MaxContextLength - entry.MaxOutputTokens;
        return result;
    }

    /// <summary>
    ///     Retains the largest tokenizer-decoded prompt prefix that fits the
    ///     model after adding the complete truncation notice and chat framing.
    ///     Subject descriptors are bounded before this point; this is the
    ///     deterministic last-resort guard for model-context safety.
    /// </summary>
    internal static string FitPromptToTokenBudget(string prompt,
                                                  int maxPromptTokens,
                                                  Func<string, int> countChatTokens,
                                                  Func<string, int, string> takePromptTokens)
    {
        ArgumentException.ThrowIfNullOrEmpty(prompt);
        if (maxPromptTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPromptTokens));
        ArgumentNullException.ThrowIfNull(countChatTokens);
        ArgumentNullException.ThrowIfNull(takePromptTokens);

        string result = prompt;
        if (countChatTokens(prompt) > maxPromptTokens)
        {
            bool hasStructuredEvidence = ClassifierPromptEvidence.TrySplit(prompt,
                                                                            out string prefix,
                                                                            out string evidence,
                                                                            out string suffix);
            result = hasStructuredEvidence
                         ? FitStructuredPromptToTokenBudget(prefix,
                                                            evidence,
                                                            suffix,
                                                            maxPromptTokens,
                                                            countChatTokens,
                                                            takePromptTokens)
                         : FitUnstructuredPromptToTokenBudget(prompt,
                                                              maxPromptTokens,
                                                              countChatTokens,
                                                              takePromptTokens);
        }

        return result;
    }

    private static string FitStructuredPromptToTokenBudget(string prefix,
                                                            string evidence,
                                                            string suffix,
                                                            int maxPromptTokens,
                                                            Func<string, int> countChatTokens,
                                                            Func<string, int, string> takePromptTokens)
    {
        string result = string.Concat(prefix, BuildTruncatedEvidence(string.Empty), suffix);
        if (countChatTokens(result) > maxPromptTokens)
            throw new InvalidOperationException(PromptBudgetTooSmallMessage);

        int lowerTokenCount = 0;
        int upperTokenCount = maxPromptTokens;
        while (lowerTokenCount <= upperTokenCount)
        {
            int candidateTokenCount = lowerTokenCount + (upperTokenCount - lowerTokenCount) / 2;
            string evidencePrefix = takePromptTokens(evidence, candidateTokenCount);
            string candidate = string.Concat(prefix,
                                             BuildTruncatedEvidence(evidencePrefix),
                                             suffix);
            int encodedTokens = countChatTokens(candidate);
            if (encodedTokens <= maxPromptTokens)
            {
                result = candidate;
                lowerTokenCount = candidateTokenCount + 1;
            }
            else
                upperTokenCount = candidateTokenCount - 1;
        }

        return result;
    }

    private static string FitUnstructuredPromptToTokenBudget(string prompt,
                                                              int maxPromptTokens,
                                                              Func<string, int> countChatTokens,
                                                              Func<string, int, string> takePromptTokens)
    {
        int noticeTokens = countChatTokens(PromptTruncationNotice);
        if (noticeTokens > maxPromptTokens)
            throw new InvalidOperationException(PromptBudgetTooSmallMessage);

        int lowerTokenCount = 0;
        int upperTokenCount = maxPromptTokens;
        string result = PromptTruncationNotice;
        while (lowerTokenCount <= upperTokenCount)
        {
            int candidateTokenCount = lowerTokenCount + (upperTokenCount - lowerTokenCount) / 2;
            string prefix = takePromptTokens(prompt, candidateTokenCount);
            string candidate = string.Concat(prefix, PromptTruncationNotice);
            int encodedTokens = countChatTokens(candidate);
            if (encodedTokens <= maxPromptTokens)
            {
                result = candidate;
                lowerTokenCount = candidateTokenCount + 1;
            }
            else
                upperTokenCount = candidateTokenCount - 1;
        }

        return result;
    }

    private static string BuildTruncatedEvidence(string serializedEvidencePrefix)
    {
        string result = JsonSerializer.Serialize(new
                                                     {
                                                         truncated = true,
                                                         notice = PromptTruncationMessage,
                                                         serializedEvidencePrefix
                                                     });
        return result;
    }

    private static int CountChatTokens(Tokenizer tokenizer, string prompt)
    {
        using var sequences = tokenizer.Encode(BuildChatPrompt(prompt));
        int result = sequences[FirstSequence].Length;
        return result;
    }

    private static string TakePromptTokens(Tokenizer tokenizer, string prompt, int maxTokens)
    {
        using var sequences = tokenizer.Encode(prompt);
        ReadOnlySpan<int> tokens = sequences[FirstSequence];
        int length = Math.Min(maxTokens, tokens.Length);
        string result = tokenizer.Decode(tokens[..length]);
        return result;
    }

    private static void ValidateEntry(ClassifierModelEntry entry)
    {
        if (entry.MaxContextLength <= 0)
            throw new ArgumentException(ContextLengthMessage, nameof(entry));
        if (entry.MaxOutputTokens <= 0 || entry.MaxOutputTokens >= entry.MaxContextLength)
            throw new ArgumentException(OutputTokenMessage, nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.Stop))
            throw new ArgumentException(StopTokenMessage, nameof(entry));
    }

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
    private const string ContextLengthMessage = "The classifier context length must be greater than zero.";
    private const string OutputTokenMessage =
        "The classifier output-token count must be greater than zero and smaller than its context length.";
    private const string StopTokenMessage = "The classifier stop token cannot be empty.";
    private const string PromptBudgetTooSmallMessage =
        "The classifier prompt-token budget cannot contain the required chat framing and truncation notice.";

    private const ulong FirstSequence = 0UL;
    private const int NoCompleteJsonObjectEnd = -1;

    private const char JsonObjectOpen = '{';
    private const char JsonObjectClose = '}';
    private const char JsonQuote = '"';
    private const char JsonEscape = '\\';

    private const string MaxLengthOption = "max_length";
    private const string TemperatureOption = "temperature";
    private const string DoSampleOption = "do_sample";

    private const string ClassifierSystemMessage = "You are a documentation classifier. Respond with ONLY the requested JSON object.";

    private const string PromptTruncationMessage =
        "Document evidence was truncated to preserve the model output budget.";
    internal const string PromptTruncationNotice =
        "\n[Document evidence was truncated to preserve the model output budget.]";

    private const string SystemTurnOpen = "<|system|>";
    private const string UserTurnOpen = "<|user|>";
    private const string AssistantTurnOpen = "<|assistant|>";
    private const string TurnClose = "<|end|>";
}
