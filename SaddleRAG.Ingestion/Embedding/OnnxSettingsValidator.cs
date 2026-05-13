// OnnxSettingsValidator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Validates <see cref="OnnxSettings" /> at startup, when the
///     <c>IOptions&lt;OnnxSettings&gt;.Value</c> is first accessed. The
///     <see cref="OnnxSettings.EmbeddingModels" /> and
///     <see cref="OnnxSettings.RerankerModels" /> registries have per-
///     <see cref="TokenizerFamily" /> file requirements (Bert needs
///     <c>VocabFile</c>, SentencePiece needs <c>SpmFile</c>) that the
///     POCO can't express in its type. Without this validator, a
///     misconfig surfaces deep inside provider construction at first
///     inference with a confusing partial stack trace; with it, the
///     host fails fast at startup with a complete error message.
///     Validation is skipped entirely when <c>Onnx.Enabled=false</c>
///     because the legacy Ollama path doesn't load any of these
///     registries.
/// </summary>
public class OnnxSettingsValidator : IValidateOptions<OnnxSettings>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, OnnxSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ValidateOptionsResult result;
        if (!options.Enabled)
            result = ValidateOptionsResult.Success;
        else
        {
            var failures = new List<string>();
            CollectEmbeddingFailures(options, failures);
            CollectRerankerFailures(options, failures);
            result = failures.Count == 0
                         ? ValidateOptionsResult.Success
                         : ValidateOptionsResult.Fail(failures);
        }

        return result;
    }

    private static void CollectEmbeddingFailures(OnnxSettings options, List<string> failures)
    {
        if (options.EmbeddingEnabled && options.EmbeddingModels.Count == 0)
            failures.Add(EmptyEmbeddingRegistryMessage);

        foreach(var entry in options.EmbeddingModels)
            ValidateEntry(entry.Name, entry.RepoId, entry.ModelFile, entry.TokenizerFamily,
                          entry.VocabFile, entry.SpmFile, failures
                         );
    }

    private static void CollectRerankerFailures(OnnxSettings options, List<string> failures)
    {
        foreach(var entry in options.RerankerModels)
        {
            ValidateEntry(entry.Name, entry.RepoId, entry.ModelFile, entry.TokenizerFamily,
                          entry.VocabFile, entry.SpmFile, failures
                         );

            if (entry.MaxSequenceLength < OnnxReRanker.MinViableSequenceLength)
                failures.Add(string.Format(MaxSequenceLengthTooSmallFormat, entry.Name,
                                           entry.MaxSequenceLength,
                                           OnnxReRanker.MinViableSequenceLength
                                          ));
        }
    }

    private static void ValidateEntry(string name,
                                      string repoId,
                                      string modelFile,
                                      TokenizerFamily family,
                                      string vocabFile,
                                      string spmFile,
                                      List<string> failures)
    {
        if (string.IsNullOrEmpty(name))
            failures.Add(EmptyNameMessage);

        if (string.IsNullOrEmpty(repoId))
            failures.Add(string.Format(MissingRepoIdFormat, name));

        if (string.IsNullOrEmpty(modelFile))
            failures.Add(string.Format(MissingModelFileFormat, name));

        switch (family)
        {
            case TokenizerFamily.Bert:
                if (string.IsNullOrEmpty(vocabFile))
                    failures.Add(string.Format(BertMissingVocabFormat, name));
                break;
            case TokenizerFamily.SentencePiece:
                if (string.IsNullOrEmpty(spmFile))
                    failures.Add(string.Format(SentencePieceMissingSpmFormat, name));
                break;
            default:
                failures.Add(string.Format(UnknownFamilyFormat, name, family));
                break;
        }
    }

    private const string EmptyEmbeddingRegistryMessage = "Onnx.EmbeddingEnabled=true but Onnx.EmbeddingModels is empty. Either disable embedding or populate at least one entry.";
    private const string EmptyNameMessage = "Onnx registry entry has an empty Name. Every entry needs a stable identifier.";
    private const string MissingRepoIdFormat = "Onnx registry entry '{0}' has no RepoId; the downloader needs this to fetch the model.";
    private const string MissingModelFileFormat = "Onnx registry entry '{0}' has no ModelFile; the downloader needs this path inside the HuggingFace repo.";
    private const string BertMissingVocabFormat = "Onnx registry entry '{0}' has TokenizerFamily=Bert but no VocabFile. BERT tokenization requires a vocab.txt path.";
    private const string SentencePieceMissingSpmFormat = "Onnx registry entry '{0}' has TokenizerFamily=SentencePiece but no SpmFile. SentencePiece tokenization requires an spm.model path.";
    private const string UnknownFamilyFormat = "Onnx registry entry '{0}' has unsupported TokenizerFamily '{1}'.";
    private const string MaxSequenceLengthTooSmallFormat = "Onnx reranker entry '{0}' has MaxSequenceLength={1}, which is below the minimum viable value ({2}). At this size the [CLS]/[SEP] overhead and doc-side floor consume the entire window, leaving zero room for query tokens; the cross-encoder would silently score 'empty query vs doc' and tank recall.";
}
