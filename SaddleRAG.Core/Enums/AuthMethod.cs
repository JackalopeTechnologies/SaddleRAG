// AuthMethod.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Authentication method for scraping protected documentation sites.
/// </summary>
public enum AuthMethod
{
    /// <summary>
    ///     Inject a pre-obtained cookie string into all requests.
    /// </summary>
    Cookie,

    /// <summary>
    ///     Automate a login form before crawling via Playwright.
    /// </summary>
    LoginForm,

    /// <summary>
    ///     Pass an API key or bearer token in a request header.
    /// </summary>
    ApiKey
}
