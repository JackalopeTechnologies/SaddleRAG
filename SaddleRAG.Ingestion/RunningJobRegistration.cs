namespace SaddleRAG.Ingestion;

/// <summary>Cancellation and database ownership for one running job.</summary>
internal sealed record RunningJobRegistration(CancellationTokenSource Cancellation, string? Profile);
