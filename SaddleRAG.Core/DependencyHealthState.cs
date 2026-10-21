// DependencyHealthState.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Core;

/// <summary>Thread-safe service observations, readable even while dependencies are unavailable.</summary>
public sealed class DependencyHealthState
{
    private readonly Lock mLock = new();
    private readonly Dictionary<string, DependencyHealth> mEntries = new(StringComparer.Ordinal);

    /// <summary>Returns an immutable copy of the latest observations.</summary>
    public IReadOnlyList<DependencyHealth> Snapshot()
    {
        lock (mLock)
            return mEntries.Values.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Records an observation and retains outage and recovery times.</summary>
    public bool Record(DependencyHealth observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        bool changed;
        lock (mLock)
        {
            mEntries.TryGetValue(observation.Name, out DependencyHealth? previous);
            changed = previous?.Status != observation.Status;
            mEntries[observation.Name] = observation with
            {
                LastSucceededAt = observation.Status == "Healthy"
                    ? observation.LastCheckedAt : previous?.LastSucceededAt,
                UnavailableSince = observation.Status == "Unavailable"
                    ? previous?.UnavailableSince ?? observation.LastCheckedAt : null
            };
        }
        return changed;
    }
}
