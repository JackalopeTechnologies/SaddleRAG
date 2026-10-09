// ManualTimeProvider.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tests.Database;

/// <summary>
///     Test clock whose monotonic time (<see cref="GetTimestamp" />) and wall-clock
///     time (<see cref="GetUtcNow" />) move independently: <see cref="Advance" />
///     moves both, <see cref="StepWallClock" /> moves only the wall clock, the way a
///     time-sync correction does.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        mUtcNow = utcNow;
    }

    private long mTimestampTicks;
    private DateTimeOffset mUtcNow;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan elapsed)
    {
        mTimestampTicks += elapsed.Ticks;
        mUtcNow += elapsed;
    }

    public void StepWallClock(TimeSpan step)
    {
        mUtcNow += step;
    }

    public override long GetTimestamp() => mTimestampTicks;

    public override DateTimeOffset GetUtcNow() => mUtcNow;
}
