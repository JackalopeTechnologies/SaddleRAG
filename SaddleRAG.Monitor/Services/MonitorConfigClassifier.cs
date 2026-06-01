// MonitorConfigClassifier.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Classifier card on the Monitor /config page. Surfaces the real active
///     backend so the page no longer implies Ollama is classifying when the
///     ONNX backend is active. <see cref="ActiveBackend" /> is the live value
///     from <c>ClassifierBackendSwitch.ActiveBackendName</c> ("onnx" or
///     "ollama"). <see cref="ActiveOnnxModel" />, <see cref="RepoId" />, and
///     <see cref="ModelFolder" /> describe the resolved ONNX classifier entry.
///     <see cref="ModelFilesPresent" /> is true when the model folder exists
///     on disk. <see cref="OllamaClassificationModel" /> shows the configured
///     Ollama classification model for reference regardless of active backend.
/// </summary>
public sealed record MonitorConfigClassifier(
    string ActiveBackend,
    string ActiveOnnxModel,
    string RepoId,
    string ModelFolder,
    bool ModelFilesPresent,
    string OllamaClassificationModel);
