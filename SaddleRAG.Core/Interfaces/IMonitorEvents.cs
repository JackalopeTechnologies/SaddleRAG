// IMonitorEvents.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Discrete lifecycle events raised by <see cref="IMonitorBroadcaster" />.
///     A relay (e.g. MonitorLifecycleRelay) subscribes to these and forwards
///     them onto SignalR for browser consumption.
/// </summary>
public interface IMonitorEvents
{
    event Action<JobStartedEvent>?   JobStarted;
    event Action<JobCompletedEvent>? JobCompleted;
    event Action<JobFailedEvent>?    JobFailed;
    event Action<JobCancelledEvent>? JobCancelled;
    event Action<SuspectFlagEvent>?  SuspectFlagRaised;
}
