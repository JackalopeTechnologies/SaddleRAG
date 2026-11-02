// DoclingOperatorIntent.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Whether the operator has deliberately stopped Docling. The tray creates one and gives it to
///     both the <see cref="ComponentController" /> and the <see cref="DoclingLaunchCoordinator" />.
///     <para>
///         The coordinator starts Docling by itself whenever the server reports a scan waiting. Without
///         this, an operator who stops Docling from the Components window while a scan waits would see
///         it start again within one poll. After the operator stops Docling the coordinator leaves it
///         alone until the operator next starts or restarts it successfully, which lifts the
///         suppression. The state is held here, apart from the launcher, because the launcher's own
///         guard answers a different question (has this session already launched Docling).
///     </para>
///     <para>Thread-safe: the controller and the coordinator may run on different threads.</para>
/// </summary>
public sealed class DoclingOperatorIntent
{
    private readonly Lock mSync = new();
    private bool mSuppressed;
    private int mSuppressionCount;

    /// <summary>True from the operator's stop of Docling until the operator's next successful start.</summary>
    public bool IsAutoStartSuppressed
    {
        get
        {
            lock(mSync)
                return mSuppressed;
        }
    }

    /// <summary>
    ///     Counts the suppressions begun so far. A caller that wants to act once per suppression
    ///     remembers the last number it acted on.
    /// </summary>
    public int SuppressionCount
    {
        get
        {
            lock(mSync)
                return mSuppressionCount;
        }
    }

    /// <summary>The operator stopped Docling: keep the coordinator from starting it again.</summary>
    public void SuppressAutoStart()
    {
        lock(mSync)
        {
            if (!mSuppressed)
            {
                mSuppressed = true;
                mSuppressionCount++;
            }
        }
    }

    /// <summary>The operator started or restarted Docling successfully: the coordinator may start it again.</summary>
    public void LiftAutoStartSuppression()
    {
        lock(mSync)
            mSuppressed = false;
    }
}
