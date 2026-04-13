// // McpWarmupState.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

namespace DocRAG.Mcp;

public sealed class McpWarmupState
{
    public string Status { get; private set; } = "NotStarted";

    public string CurrentPhase { get; private set; } = "Idle";

    public string? LastError { get; private set; }
    private readonly object mLock = new object();

    public void MarkStarted(string phase)
    {
        ArgumentException.ThrowIfNullOrEmpty(phase);
        lock(mLock)
        {
            Status = "Running";
            CurrentPhase = phase;
            LastError = null;
        }
    }

    public void MarkPhase(string phase)
    {
        ArgumentException.ThrowIfNullOrEmpty(phase);
        lock(mLock)
        {
            CurrentPhase = phase;
        }
    }

    public void MarkCompleted(string phase)
    {
        ArgumentException.ThrowIfNullOrEmpty(phase);
        lock(mLock)
        {
            Status = "Completed";
            CurrentPhase = phase;
            LastError = null;
        }
    }

    public void MarkFailed(string phase, string error)
    {
        ArgumentException.ThrowIfNullOrEmpty(phase);
        ArgumentException.ThrowIfNullOrEmpty(error);
        lock(mLock)
        {
            Status = "Failed";
            CurrentPhase = phase;
            LastError = error;
        }
    }
}
