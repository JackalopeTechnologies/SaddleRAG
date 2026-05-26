// EscalationController.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Per-crawl controller that starts with the SSR navigator and
///     swaps to the SPA navigator when shell sniffing detects a known
///     single-page application framework in the response body, or when
///     the caller supplied an explicit <see cref="ScrapeJob.WaitForSelector" />.
///     <para>
///         The swap is committed atomically inside the controller's lock:
///         <see cref="Active" /> flips to the SPA navigator, the URL list
///         is snapshotted, and the controller is marked escalated. The
///         logger / onEscalate callback / DryRunAccumulator notification
///         all run AFTER the lock releases so a callback can synchronously
///         touch the controller without risk of deadlock.
///     </para>
/// </summary>
public sealed class EscalationController
{
    public EscalationController(ScrapeJob job,
                                IPageNavigator initial,
                                IPageNavigator spa,
                                Action<IReadOnlyList<string>>? onEscalate,
                                DryRunAccumulator? dryRunAcc,
                                ILogger<EscalationController> logger)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(spa);
        ArgumentNullException.ThrowIfNull(logger);

        mJob = job;
        mSpa = spa;
        mActive = initial;
        mOnEscalate = onEscalate;
        mDryRunAcc = dryRunAcc;
        mLogger = logger;
    }

    private readonly Lock mLock = new();
    private readonly List<string> mFetchedUrls = new();
    private readonly ScrapeJob mJob;
    private readonly IPageNavigator mSpa;
    private readonly Action<IReadOnlyList<string>>? mOnEscalate;
    private readonly DryRunAccumulator? mDryRunAcc;
    private readonly ILogger<EscalationController> mLogger;
    private IPageNavigator mActive;
    private bool mEscalated;

    /// <summary>
    ///     The navigator currently in use. Reads of this property are
    ///     lock-protected — workers can safely query it on every page
    ///     fetch without external synchronization.
    /// </summary>
    public IPageNavigator Active
    {
        get
        {
            IPageNavigator result;
            lock(mLock)
                result = mActive;
            return result;
        }
    }

    /// <summary>
    ///     True once the controller has swapped to the SPA navigator.
    ///     Stays true for the remainder of the crawl.
    /// </summary>
    public bool ShouldEscalate
    {
        get
        {
            bool result;
            lock(mLock)
                result = mEscalated;
            return result;
        }
    }

    /// <summary>
    ///     Observe a successfully fetched page. Adds the URL to the
    ///     internal requeue tracking list and, while the detection
    ///     window is open, runs shell sniffing on the response body.
    ///     A non-null signal — or a non-null
    ///     <see cref="ScrapeJob.WaitForSelector" /> on the first page —
    ///     triggers escalation.
    ///     <para>
    ///         Returns <c>true</c> when THIS call is the one that triggered
    ///         escalation. The caller uses that to skip persisting the
    ///         in-flight page record: the trigger page is in the requeue
    ///         list and will be re-fetched under the SPA navigator, so the
    ///         current SSR-rendered DOM would only race the re-fetch into
    ///         the upsert. Returns <c>false</c> for every other
    ///         observation, including ones made while
    ///         <see cref="ShouldEscalate" /> is already true.
    ///     </para>
    /// </summary>
    public bool ObservePage(string url, string responseText)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentNullException.ThrowIfNull(responseText);

        SpaFramework? framework = null;
        string? reason = null;
        IReadOnlyList<string>? urlsSnapshot = null;

        lock(mLock)
        {
            mFetchedUrls.Add(url);

            bool windowOpen = !mEscalated && mFetchedUrls.Count <= EscalationWindowPages;
            if (windowOpen)
            {
                var detection = Detect(responseText);
                if (detection.HasValue)
                {
                    framework = detection.Value.Framework;
                    reason = detection.Value.Reason;
                    mActive = mSpa;
                    mEscalated = true;
                    urlsSnapshot = mFetchedUrls.ToArray();
                }
            }
        }

        if (framework.HasValue && reason != null && urlsSnapshot != null)
            NotifyEscalation(framework.Value, reason, urlsSnapshot);

        return framework.HasValue;
    }

    private (SpaFramework Framework, string Reason)? Detect(string responseText)
    {
        bool forceFromSelector = !string.IsNullOrEmpty(mJob.WaitForSelector) && mFetchedUrls.Count == 1;

        (SpaFramework Framework, string Reason)? result = null;
        if (forceFromSelector)
            result = (SpaFramework.UserSupplied, UserSuppliedSelectorReason);
        else
        {
            var match = DetectShellSignal(responseText);
            if (match.HasValue)
                result = match.Value;
        }

        return result;
    }

    private void NotifyEscalation(SpaFramework framework, string reason, IReadOnlyList<string> urls)
    {
        mLogger.LogWarning("SPA escalation triggered after {Pages} pages by signal [{Signal}] (framework={Framework}). " +
                           "Switching to SPA navigator. Re-queuing {Count} already-fetched pages.",
                           urls.Count,
                           reason,
                           framework,
                           urls.Count
                          );

        SafeInvokeOnEscalate(urls);
        mDryRunAcc?.RecordNavigatorSwap(framework, reason);
    }

    private void SafeInvokeOnEscalate(IReadOnlyList<string> urls)
    {
        if (mOnEscalate != null)
        {
            try
            {
                mOnEscalate.Invoke(urls);
            }
            catch(ObjectDisposedException ex)
            {
                mLogger.LogError(ex,
                                 "EscalationController onEscalate threw ObjectDisposedException; navigator swap committed but {Count} URLs may not have been requeued",
                                 urls.Count
                                );
            }
            catch(InvalidOperationException ex)
            {
                mLogger.LogError(ex,
                                 "EscalationController onEscalate threw InvalidOperationException; navigator swap committed but {Count} URLs may not have been requeued",
                                 urls.Count
                                );
            }
        }
    }

    /// <summary>
    ///     Pure function: returns the matching framework and the
    ///     human-readable reason when <paramref name="html" /> contains a
    ///     known SPA shell marker, null otherwise. Contents of
    ///     <c>&lt;pre&gt;</c>, <c>&lt;code&gt;</c>, and <c>&lt;noscript&gt;</c>
    ///     blocks are removed before matching so tutorial pages teaching
    ///     framework setup (which print HTML-escaped script tags such as
    ///     <c>&amp;lt;script src=&amp;quot;_framework/blazor.server.js&amp;quot;&amp;gt;</c>
    ///     in code samples) do not trigger false-positive escalation.
    /// </summary>
    private static (SpaFramework Framework, string Reason)? DetectShellSignal(string html)
    {
        string scanText = smCodeBlockPattern.Replace(html, string.Empty);
        (SpaFramework Framework, string Reason)? result = smShellMarkers
            .Where(m => scanText.Contains(m.Marker, StringComparison.Ordinal))
            .Select(m => ((SpaFramework, string)?) (m.Framework, m.Reason))
            .FirstOrDefault();
        return result;
    }

    private static readonly Regex smCodeBlockPattern = new(@"<(pre|code|noscript)\b[^>]*>.*?</\1\s*>",
                                                           RegexOptions.Compiled
                                                           | RegexOptions.Singleline
                                                           | RegexOptions.IgnoreCase
                                                          );

    private static readonly (SpaFramework Framework, string Marker, string Reason)[] smShellMarkers =
        [
            (SpaFramework.BlazorWasm, BlazorWasmMarker, BlazorWasmReason),
            (SpaFramework.BlazorWasm, BlazorFrameworkMarker, BlazorFrameworkReason),
            (SpaFramework.BlazorWasm, BlazorBootMarker, BlazorBootReason),
            (SpaFramework.NextJsCsr, NextDataMarker, NextDataReason),
            (SpaFramework.NuxtCsr, NuxtMarker, NuxtReason),
            (SpaFramework.SvelteCsr, SvelteKitMarker, SvelteKitReason),
            (SpaFramework.ReactCsr, ReactRootMarker, ReactRootReason),
            (SpaFramework.VueCsr, VueAppMarker, VueAppReason),
            (SpaFramework.AngularCsr, AngularVersionMarker, AngularVersionReason),
        ];

    private const string BlazorWasmMarker = "blazor.webassembly.js";
    private const string BlazorFrameworkMarker = "_framework/blazor";
    private const string BlazorBootMarker = "<blazor-boot";
    private const string NextDataMarker = "__NEXT_DATA__";
    private const string NuxtMarker = "__NUXT__";
    private const string SvelteKitMarker = "data-sveltekit-";
    private const string ReactRootMarker = "data-reactroot";
    private const string VueAppMarker = "data-v-app";
    private const string AngularVersionMarker = "ng-version=";

    private const string BlazorWasmReason = "Blazor WASM detected via blazor.webassembly.js script";
    private const string BlazorFrameworkReason = "Blazor WASM detected via _framework/blazor path";
    private const string BlazorBootReason = "Blazor WASM detected via <blazor-boot> tag";
    private const string NextDataReason = "Next.js CSR detected via __NEXT_DATA__";
    private const string NuxtReason = "Nuxt CSR detected via __NUXT__";
    private const string SvelteKitReason = "Svelte CSR detected via data-sveltekit- attribute";
    private const string ReactRootReason = "React CSR detected via data-reactroot attribute";
    private const string VueAppReason = "Vue CSR detected via data-v-app attribute";
    private const string AngularVersionReason = "Angular detected via ng-version attribute";

    private const int EscalationWindowPages = 3;
    private const string UserSuppliedSelectorReason = "User-supplied waitForSelector forces SPA mode";
}
