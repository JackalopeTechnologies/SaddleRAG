// SpaFramework.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Single-page application framework that triggered crawler escalation.
///     A value of this enum appears on <c>NavigatorEscalation.Framework</c>
///     so tests and downstream consumers can assert against a typed signal
///     rather than parsing the human-readable reason string.
/// </summary>
public enum SpaFramework
{
    /// <summary>
    ///     Blazor WebAssembly. Detected via the
    ///     <c>blazor.webassembly.js</c> script tag or the
    ///     <c>_framework/blazor</c> path in raw HTML.
    /// </summary>
    BlazorWasm,

    /// <summary>
    ///     Next.js client-side rendering. Detected via the
    ///     <c>__NEXT_DATA__</c> JSON island.
    /// </summary>
    NextJsCsr,

    /// <summary>
    ///     Nuxt client-side rendering. Detected via the
    ///     <c>__NUXT__</c> JSON island.
    /// </summary>
    NuxtCsr,

    /// <summary>
    ///     SvelteKit client-side rendering. Detected via the
    ///     <c>data-sveltekit-</c> attribute family.
    /// </summary>
    SvelteCsr,

    /// <summary>
    ///     React client-side rendering. Detected via the
    ///     <c>data-reactroot</c> attribute.
    /// </summary>
    ReactCsr,

    /// <summary>
    ///     Vue client-side rendering. Detected via the
    ///     <c>data-v-app</c> attribute.
    /// </summary>
    VueCsr,

    /// <summary>
    ///     Angular client-side rendering. Detected via the
    ///     <c>ng-version</c> attribute.
    /// </summary>
    AngularCsr,

    /// <summary>
    ///     Not a framework detection — the caller explicitly forced SPA
    ///     mode via <c>ScrapeJob.WaitForSelector</c>.
    /// </summary>
    UserSupplied
}
