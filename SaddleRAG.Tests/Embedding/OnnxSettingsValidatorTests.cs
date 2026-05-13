// OnnxSettingsValidatorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OnnxSettingsValidatorTests
{
    [Fact]
    public void DisabledSettingsAlwaysValid()
    {
        var validator = new OnnxSettingsValidator();
        var settings = new OnnxSettings { Enabled = false };

        var result = validator.Validate(name: null, settings);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void EnabledWithEmptyEmbeddingRegistryAndEmbeddingEnabledFailsValidation()
    {
        var validator = new OnnxSettingsValidator();
        var settings = new OnnxSettings { Enabled = true, EmbeddingEnabled = true };

        var result = validator.Validate(name: null, settings);

        Assert.True(result.Failed);
        Assert.Contains("EmbeddingModels is empty", result.FailureMessage);
    }

    [Fact]
    public void EnabledWithBertEntryMissingVocabFileFailsValidation()
    {
        var validator = new OnnxSettingsValidator();
        var settings = new OnnxSettings { Enabled = true };
        settings.EmbeddingModels.Add(new EmbeddingModelEntry
                                         {
                                             Name = "bert-model",
                                             RepoId = "test/bert",
                                             ModelFile = "model.onnx",
                                             TokenizerFamily = TokenizerFamily.Bert
                                         });

        var result = validator.Validate(name: null, settings);

        Assert.True(result.Failed);
        Assert.Contains("VocabFile", result.FailureMessage);
        Assert.Contains("bert-model", result.FailureMessage);
    }

    [Fact]
    public void EnabledWithSentencePieceRerankerMissingSpmFileFailsValidation()
    {
        var validator = new OnnxSettingsValidator();
        var settings = new OnnxSettings { Enabled = true };
        settings.RerankerModels.Add(new RerankerModelEntry
                                        {
                                            Name = "sp-reranker",
                                            RepoId = "test/sp",
                                            ModelFile = "model.onnx",
                                            TokenizerFamily = TokenizerFamily.SentencePiece
                                        });

        var result = validator.Validate(name: null, settings);

        Assert.True(result.Failed);
        Assert.Contains("SpmFile", result.FailureMessage);
        Assert.Contains("sp-reranker", result.FailureMessage);
    }

    [Fact]
    public void EnabledWithEntryMissingNameOrRepoIdFailsValidation()
    {
        var validator = new OnnxSettingsValidator();
        var settings = new OnnxSettings { Enabled = true };
        settings.EmbeddingModels.Add(new EmbeddingModelEntry
                                         {
                                             Name = string.Empty,
                                             RepoId = string.Empty,
                                             ModelFile = string.Empty,
                                             TokenizerFamily = TokenizerFamily.Bert,
                                             VocabFile = "vocab.txt"
                                         });

        var result = validator.Validate(name: null, settings);

        Assert.True(result.Failed);
        Assert.Contains("empty Name", result.FailureMessage);
    }

    [Fact]
    public void EnabledWithWellFormedRegistryPasses()
    {
        var validator = new OnnxSettingsValidator();
        var settings = new OnnxSettings { Enabled = true };
        settings.EmbeddingModels.Add(new EmbeddingModelEntry
                                         {
                                             Name = "nomic",
                                             RepoId = "nomic-ai/nomic-embed-text-v1.5",
                                             ModelFile = "onnx/model.onnx",
                                             TokenizerFamily = TokenizerFamily.Bert,
                                             VocabFile = "vocab.txt"
                                         });
        settings.RerankerModels.Add(new RerankerModelEntry
                                        {
                                            Name = "mxbai",
                                            RepoId = "mixedbread-ai/mxbai-rerank-base-v1",
                                            ModelFile = "onnx/model.onnx",
                                            TokenizerFamily = TokenizerFamily.SentencePiece,
                                            SpmFile = "spm.model"
                                        });

        var result = validator.Validate(name: null, settings);

        Assert.True(result.Succeeded);
    }
}
