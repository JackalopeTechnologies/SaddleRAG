// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OllamaSharp.Models;
using SaddleRAG.Ingestion.Classification;

namespace SaddleRAG.ClassifierComparison;

internal sealed class RecordedGenerateClient(IOllamaGenerateClient inner, HttpClient http, string output, string model)
    : IOllamaGenerateClient
{
    internal const int ContextTokens = 8192;
    internal const int OutputTokens = 2048;
    internal const int CpuThreads = 2;
    internal const int Seed = 42;
    private const int PollMilliseconds = 250;
    private const string RequestSuffix = "-request.json";
    private const string ReplySuffix = "-reply.txt";
    private const string PlacementPath = "/api/ps";
    private const string ModelsProperty = "models";
    private const string ModelProperty = "model";
    private const string SizeProperty = "size";
    private const string GpuSizeProperty = "size_vram";
    internal int Calls { get; private set; }
    internal long PeakGpuBytes { get; private set; }
    internal bool PlacementObserved { get; private set; }
    private string mPlacementFailure = string.Empty;

    internal void ResetPlacement()
    {
        PeakGpuBytes = 0;
        PlacementObserved = false;
        mPlacementFailure = string.Empty;
    }

    public async IAsyncEnumerable<GenerateResponseStream?> GenerateAsync(GenerateRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        int sequence = ++Calls;
        request.Options = new RequestOptions
        {
            Temperature = 0, Seed = Seed, NumCtx = ContextTokens, NumPredict = OutputTokens,
            NumThread = CpuThreads, NumGpu = -1
        };
        request.KeepAlive = "0";
        request.Think = false;
        string prefix = Path.Combine(output, $"call-{sequence:D3}");
        await File.WriteAllTextAsync(prefix + RequestSuffix, JsonSerializer.Serialize(request, Program.JsonOptions), ct);
        var response = new StringBuilder();
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var monitorCancellation = new CancellationTokenSource();
        Task monitor = ObservePlacementAsync(requestCancellation, monitorCancellation.Token);
        try
        {
            await foreach (var token in inner.GenerateAsync(request, requestCancellation.Token))
            {
                response.Append(token?.Response);
                yield return token;
            }
        }
        finally
        {
            await monitorCancellation.CancelAsync();
            await monitor;
            await File.WriteAllTextAsync(prefix + ReplySuffix, response.ToString(), CancellationToken.None);
            if (mPlacementFailure.Length > 0)
                throw new InvalidOperationException(mPlacementFailure);
        }
    }

    private async Task ObservePlacementAsync(CancellationTokenSource requestCancellation, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ObserveSnapshotAsync(requestCancellation, ct);
                await Task.Delay(PollMilliseconds, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Monitoring ends when the corresponding request finishes.
        }
        catch (Exception exception)
        {
            mPlacementFailure = $"Graphics placement monitoring failed: {exception.Message}";
            await requestCancellation.CancelAsync();
        }
    }

    private async Task ObserveSnapshotAsync(CancellationTokenSource requestCancellation, CancellationToken ct)
    {
        using var response = await http.GetAsync(PlacementPath, ct);
        response.EnsureSuccessStatusCode();
        using var snapshot = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        foreach (var entry in snapshot.RootElement.GetProperty(ModelsProperty).EnumerateArray())
            await ObserveModelAsync(entry, requestCancellation);
    }

    private async Task ObserveModelAsync(JsonElement entry, CancellationTokenSource requestCancellation)
    {
        if (entry.GetProperty(ModelProperty).GetString() == model)
        {
            long size = entry.GetProperty(SizeProperty).GetInt64();
            long gpuBytes = entry.GetProperty(GpuSizeProperty).GetInt64();
            PeakGpuBytes = Math.Max(PeakGpuBytes, gpuBytes);
            PlacementObserved = true;
            if (gpuBytes < size)
            {
                mPlacementFailure = $"GPU-only placement failed: model size={size}, graphics allocation={gpuBytes}.";
                await requestCancellation.CancelAsync();
            }
        }
    }
}
