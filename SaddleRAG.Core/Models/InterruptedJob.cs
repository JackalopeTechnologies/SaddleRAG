namespace SaddleRAG.Core.Models;

/// <summary>A stopped job awaiting persistence of its final failure status.</summary>
public sealed record InterruptedJob(string JobId, string? Profile, string Detail);
