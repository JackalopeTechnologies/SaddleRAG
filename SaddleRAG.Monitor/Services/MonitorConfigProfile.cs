// MonitorConfigProfile.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Profile card on the Monitor /config page (issue #73).
///     <see cref="EffectiveProfile" /> resolves the
///     <c>SADDLERAG_MONGODB_PROFILE</c> environment override against
///     <c>SaddleRagDbSettings.ActiveProfile</c>, surfacing whichever
///     name the running process actually loaded settings from. Falls
///     back to <c>(direct)</c> when no profile is in effect.
/// </summary>
public sealed record MonitorConfigProfile(string EffectiveProfile);
