// MonitorConfigSnapshot.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Read-only snapshot of the SaddleRAG MCP runtime configuration as
///     surfaced by the Monitor /config page (issue #73). Built in
///     <see cref="IMonitorConfigSource.GetSnapshot" /> from the live
///     options + runtime-capability objects in the host process.
/// </summary>
public sealed record MonitorConfigSnapshot(
    MonitorConfigClassifier Classifier,
    MonitorConfigEmbedding Embedding,
    MonitorConfigReranker Reranker,
    MonitorConfigExecutionProvider ExecutionProvider,
    MonitorConfigMongo Mongo,
    MonitorConfigOllama Ollama,
    MonitorConfigProfile Profile,
    MonitorConfigCrash? Crash = null);
