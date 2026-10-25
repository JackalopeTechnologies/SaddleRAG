// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

namespace SaddleRAG.Tests.Embedding;

public sealed class OnnxClassifierLoadDiagnosticsTests
{
    [Fact]
    public void SuccessfulKnownModelRetainsCompleteNativeCautionAtInformation()
    {
        var logger = new NativeDiagnosticTestLogger();
        using (var scope = new OnnxClassifierLoadDiagnostics(true, logger))
        {
            SendExpectedSequence(scope.LogId);
            Assert.Empty(logger.Entries);
            scope.Complete(true);
        }
        Assert.Equal(3, logger.Entries.Count);
        Assert.All(logger.Entries, item => Assert.Equal(LogLevel.Information, item.Level));
        Assert.Contains(logger.Entries, item => item.Message.EndsWith(OnnxClassifierLoadDiagnostics.ShapeMessage, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UnreviewedOrFailedLoadsKeepWarnings(bool eligible, bool completed)
    {
        var logger = new NativeDiagnosticTestLogger();
        using (var scope = new OnnxClassifierLoadDiagnostics(eligible, logger))
        {
            SendExpectedSequence(scope.LogId);
            if (completed)
                scope.Complete(true);
        }
        Assert.Equal(3, logger.Entries.Count);
        Assert.All(logger.Entries, item => Assert.Equal(LogLevel.Warning, item.Level));
    }

    [Fact]
    public void IncompleteSequenceRemainsWarning()
    {
        var logger = new NativeDiagnosticTestLogger();
        using (var scope = new OnnxClassifierLoadDiagnostics(true, logger))
        {
            Send(scope.LogId, OnnxClassifierLoadDiagnostics.ShapeMessage, ShapeLocation);
            scope.Complete(true);
        }
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Theory]
    [InlineData(OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING, LogLevel.Warning)]
    [InlineData(OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR, LogLevel.Error)]
    [InlineData(OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL, LogLevel.Critical)]
    public void AdditionalProblemRemainsVisibleAndRejectsReclassification(OrtLoggingLevel severity, LogLevel expected)
    {
        var logger = new NativeDiagnosticTestLogger();
        using (var scope = new OnnxClassifierLoadDiagnostics(true, logger))
        {
            SendExpectedSequence(scope.LogId);
            Assert.True(OnnxClassifierLoadDiagnostics.TryReceive(severity, NativeCategory, scope.LogId,
                ShapeLocation, "DXGI_ERROR_DEVICE_REMOVED"));
            Assert.Equal(expected, Assert.Single(logger.Entries).Level);
            scope.Complete(true);
        }
        Assert.Equal(4, logger.Entries.Count);
        Assert.Equal(3, logger.Entries.Count(item => item.Message.Contains("Native onnxruntime", StringComparison.Ordinal) &&
                                                    item.Level == LogLevel.Warning &&
                                                    !item.Message.Contains("DXGI_ERROR_DEVICE_REMOVED", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("other", ShapeLocation, OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING)]
    [InlineData(NativeCategory, "application.cs:2444 onnxruntime::InferenceSession::Initialize", OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING)]
    [InlineData(NativeCategory, ShapeLocation, OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR)]
    public void MatchingWordsFromAnotherSourceOrSeverityAreNotReclassified(string category, string location, OrtLoggingLevel severity)
    {
        var logger = new NativeDiagnosticTestLogger();
        using (var scope = new OnnxClassifierLoadDiagnostics(true, logger))
        {
            Assert.True(OnnxClassifierLoadDiagnostics.TryReceive(severity, category, scope.LogId, location,
                OnnxClassifierLoadDiagnostics.ShapeMessage));
            Send(scope.LogId, OnnxClassifierLoadDiagnostics.PlacementMessage, PlacementLocation);
            Send(scope.LogId, OnnxClassifierLoadDiagnostics.VerboseMessage, PlacementLocation);
            scope.Complete(true);
        }
        Assert.Equal(3, logger.Entries.Count);
        Assert.DoesNotContain(logger.Entries, item => item.Level == LogLevel.Information);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateOrReorderedSequenceKeepsEveryRecord(bool duplicate)
    {
        var logger = new NativeDiagnosticTestLogger();
        using (var scope = new OnnxClassifierLoadDiagnostics(true, logger))
        {
            Send(scope.LogId, duplicate ? OnnxClassifierLoadDiagnostics.ShapeMessage :
                OnnxClassifierLoadDiagnostics.PlacementMessage, duplicate ? ShapeLocation : PlacementLocation);
            SendExpectedSequence(scope.LogId);
            scope.Complete(true);
        }
        Assert.Equal(4, logger.Entries.Count);
        Assert.All(logger.Entries, item => Assert.Equal(LogLevel.Warning, item.Level));
    }

    [Fact]
    public async Task ConcurrentScopesRouteOnlyTheirOwnRecordsAndReleaseTheirIdentifiers()
    {
        var firstLogger = new NativeDiagnosticTestLogger();
        var secondLogger = new NativeDiagnosticTestLogger();
        var first = new OnnxClassifierLoadDiagnostics(true, firstLogger);
        var second = new OnnxClassifierLoadDiagnostics(false, secondLogger);
        Assert.NotEqual(first.LogId, second.LogId);
        await Task.WhenAll(Task.Run(() => SendExpectedSequence(first.LogId), TestContext.Current.CancellationToken),
            Task.Run(() => SendExpectedSequence(second.LogId), TestContext.Current.CancellationToken));
        first.Complete(true);
        second.Complete(true);
        first.Dispose();
        second.Dispose();
        first.Dispose();
        Assert.Equal(3, firstLogger.Entries.Count);
        Assert.Equal(3, secondLogger.Entries.Count);
        Assert.All(firstLogger.Entries, item => Assert.Equal(LogLevel.Information, item.Level));
        Assert.All(secondLogger.Entries, item => Assert.Equal(LogLevel.Warning, item.Level));
        Assert.False(OnnxClassifierLoadDiagnostics.TryReceive(OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
            NativeCategory, first.LogId, ShapeLocation, OnnxClassifierLoadDiagnostics.ShapeMessage));
    }

    [Fact]
    public void ConfigurationOverlayContainsOnlyTheUniqueSessionLabel()
    {
        using var scope = new OnnxClassifierLoadDiagnostics(true, null);
        var expected = new JsonObject
        {
            ["model"] = new JsonObject
            {
                ["decoder"] = new JsonObject
                {
                    ["session_options"] = new JsonObject { ["log_id"] = scope.LogId }
                }
            }
        };
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(scope.CreateConfigurationOverlay())));
    }

    [Fact]
    public void ReviewedConfigurationDoesNotRequireAnExtraGraphCaptureSetting()
    {
        Assert.True(OnnxClassifierDiagnosticEligibility.IsEligible(KnownEntry(), KnownConfiguration, ReviewedGraphHash));
    }

    [Theory]
    [InlineData("phi3", "phi4")]
    [InlineData("\"dml\":{}", "\"cpu\":{}")]
    [InlineData("\"dml\":{}", "\"dml\":{},\"cuda\":{}")]
    [InlineData("true", "false")]
    [InlineData("\"num_beams\":1", "\"num_beams\":2")]
    [InlineData("model.onnx", "another.onnx")]
    [InlineData("\"num_beams\":1", "\"num_beams\":\"1\"")]
    [InlineData("\"dml\":{}", "\"dml\":{\"device_index\":1}")]
    [InlineData("\"session_options\":{", "\"session_options\":{\"ep.dml.enable_graph_capture\":\"0\",")]
    [InlineData("\"num_return_sequences\":1", "\"num_return_sequences\":2")]
    [InlineData("\"search\":{", "\"search\":{\"batch_size\":2,")]
    [InlineData("\"search\":{", "\"search\":{\"chunk_size\":64,")]
    [InlineData("\"decoder\":{", "\"decoder\":{\"sliding_window\":{},")]
    public void ChangedModelOrCapturePreconditionsRemainWarnings(string original, string replacement)
    {
        Assert.False(OnnxClassifierDiagnosticEligibility.IsEligible(KnownEntry(), KnownConfiguration.Replace(original, replacement), ReviewedGraphHash));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("repository")]
    [InlineData("folder")]
    public void AnotherRegistryIdentityCannotQualify(string field)
    {
        ClassifierModelEntry entry = KnownEntry();
        switch (field)
        {
            case "name": entry.Name = "custom-model"; break;
            case "repository": entry.RepoId = "another/repository"; break;
            case "folder": entry.ModelFolder = "cpu/model"; break;
        }
        Assert.False(OnnxClassifierDiagnosticEligibility.IsEligible(entry, KnownConfiguration, ReviewedGraphHash));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"model\":\"phi3\"}")]
    [InlineData("{\"model\":{},\"model\":{}}")]
    public void UnreadableOrAmbiguousConfigurationDoesNotQualify(string configuration)
    {
        Assert.False(OnnxClassifierDiagnosticEligibility.IsEligible(KnownEntry(), configuration, ReviewedGraphHash));
    }

    [Theory]
    [InlineData("changed-graph")]
    [InlineData("")]
    public void ChangedOrUnavailableGraphFingerprintCannotQualify(string graphHash)
    {
        Assert.False(OnnxClassifierDiagnosticEligibility.IsEligible(KnownEntry(), KnownConfiguration, graphHash));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2109331)]
    [InlineData(2147483648L)]
    public void DifferentGraphLengthDoesNotQualifyEvenWithSuppliedKnownHash(long length)
    {
        Assert.False(OnnxClassifierDiagnosticEligibility.IsEligible(KnownEntry(), KnownConfiguration, ReviewedGraphHash, length));
    }

    [Theory]
    [InlineData("1.24.4", "0.14.1", true)]
    [InlineData("1.24.5", "0.14.1", false)]
    [InlineData("1.24.4", "0.15.0", false)]
    [InlineData("1.24.4", null, false)]
    [InlineData(null, "0.14.1", false)]
    public void OnlyReviewedRuntimePairQualifies(string? runtime, string? genAi, bool expected)
    {
        Assert.Equal(expected, OnnxClassifierDiagnosticEligibility.MatchesRuntime(runtime, genAi));
    }

    [Fact]
    public void SuccessfulLoadWithUnknownRuntimeKeepsWarnings()
    {
        var logger = new NativeDiagnosticTestLogger();
        using (var scope = new OnnxClassifierLoadDiagnostics(true, logger))
        {
            SendExpectedSequence(scope.LogId);
            scope.Complete(false);
        }
        Assert.Equal(3, logger.Entries.Count);
        Assert.All(logger.Entries, item => Assert.Equal(LogLevel.Warning, item.Level));
    }

    [Fact]
    public void UnknownNativeSeverityCannotQualifyTheBatch()
    {
        var logger = new NativeDiagnosticTestLogger();
        using (var scope = new OnnxClassifierLoadDiagnostics(true, logger))
        {
            SendExpectedSequence(scope.LogId);
            Assert.True(OnnxClassifierLoadDiagnostics.TryReceive((OrtLoggingLevel)(-1), NativeCategory,
                scope.LogId, PlacementLocation, "Unknown native severity"));
            scope.Complete(true);
        }
        Assert.Equal(4, logger.Entries.Count);
        Assert.All(logger.Entries, item => Assert.Equal(LogLevel.Warning, item.Level));
    }

    [Fact]
    public void FailedLogSinkDoesNotTurnSuccessfulOrFailedLoadIntoAnotherException()
    {
        var logger = new NativeDiagnosticTestLogger { ThrowOnLog = true };
        using (var scope = new OnnxClassifierLoadDiagnostics(true, logger))
        {
            SendExpectedSequence(scope.LogId);
            scope.Complete(true);
        }
        using (var scope = new OnnxClassifierLoadDiagnostics(true, logger))
            SendExpectedSequence(scope.LogId);
    }

    [Fact]
    public void UnavailableConfigurationLeavesWarningsWithoutAddingALoadFailure()
    {
        var logger = new NativeDiagnosticTestLogger();
        string missingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using (var scope = OnnxClassifierLoadDiagnostics.Begin(KnownEntry(), missingDirectory, logger))
        {
            SendExpectedSequence(scope.LogId);
            scope.Complete(true);
        }
        Assert.Equal(3, logger.Entries.Count);
        Assert.All(logger.Entries, item => Assert.Equal(LogLevel.Warning, item.Level));
    }

    private static ClassifierModelEntry KnownEntry() => new()
    {
        Name = OnnxSettings.Phi3MiniDirectMlName,
        RepoId = OnnxSettings.Phi3MiniRepoId,
        ModelFolder = OnnxSettings.Phi3MiniDirectMlFolder
    };

    private static void SendExpectedSequence(string logId)
    {
        Send(logId, OnnxClassifierLoadDiagnostics.ShapeMessage, ShapeLocation);
        Send(logId, OnnxClassifierLoadDiagnostics.PlacementMessage, PlacementLocation);
        Send(logId, OnnxClassifierLoadDiagnostics.VerboseMessage, PlacementLocation);
    }

    private static void Send(string logId, string message, string location)
    {
        Assert.True(OnnxClassifierLoadDiagnostics.TryReceive(OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
            NativeCategory, logId, location, message));
    }

    private const string NativeCategory = "onnxruntime";
    private const string ShapeLocation = "inference_session.cc:2444 onnxruntime::InferenceSession::Initialize";
    private const string PlacementLocation = "session_state.cc:1327 onnxruntime::VerifyEachNodeIsAssignedToAnEp";
    private const string ReviewedGraphHash = OnnxClassifierDiagnosticEligibility.ReviewedGraphSha256;
    private const string KnownConfiguration = """
        {"model":{"type":"phi3","decoder":{"filename":"model.onnx","session_options":{"provider_options":[{"dml":{}}]}}},"search":{"past_present_share_buffer":true,"num_beams":1,"num_return_sequences":1}}
        """;
}
