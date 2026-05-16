// MonitorConfigMongo.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     MongoDB card on the Monitor /config page (issue #73).
///     <see cref="Host" /> is the connection string with the
///     user:password segment masked to <c>***:***</c>;
///     <see cref="CredentialsPresent" /> tells the page whether any
///     credentials were actually configured (vs. an anonymous local
///     connection).
/// </summary>
public sealed record MonitorConfigMongo(
    string ActiveProfileName,
    string Host,
    string DatabaseName,
    bool CredentialsPresent);
