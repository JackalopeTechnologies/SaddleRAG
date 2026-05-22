// EscalationController.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Per-crawl controller that starts with the SSR navigator and
///     swaps to the SPA navigator when shell sniffing detects a known
///     single-page application framework in the response body, or when
///     the caller supplied an explicit <see cref="ScrapeJob.WaitForSelector" />.
///     <para>
///         When escalation fires, three things happen atomically under
///         the controller's lock: (1) <see cref="Active" /> swaps to the
///         SPA navigator for all subsequent <c>NavigateAsync</c> calls,
///         (2) the supplied <c>onEscalate</c> callback is invoked with
///         the URLs already fetched under the SSR navigator so the
///         crawler can re-queue them — those pages were captured with
///         the wrong navigator and likely have empty content,
///         (3) the optional <see cref="DryRunAccumulator" /> records the
///         escalation reason for the dry-run report.
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

        bool result = false;
        lock(mLock)
        {
            mFetchedUrls.Add(url);

            bool windowOpen = !mEscalated && mFetchedUrls.Count <= EscalationWindowPages;
            if (windowOpen)
                result = EvaluateForEscalation(responseText);
        }

        return result;
    }

    private bool EvaluateForEscalation(string responseText)
    {
        bool forceFromSelector = !string.IsNullOrEmpty(mJob.WaitForSelector) && mFetchedUrls.Count == 1;
        string? reason = forceFromSelector
                             ? UserSuppliedSelectorReason
                             : DetectShellSignal(responseText);

        bool result = false;
        if (reason != null)
        {
            Escalate(reason);
            result = true;
        }

        return result;
    }

    private void Escalate(string reason)
    {
        mActive = mSpa;
        mEscalated = true;

        mLogger.LogWarning("SPA escalation triggered after {Pages} pages by signal [{Signal}]. " +
                           "Switching to SPA navigator. Re-queuing {Count} already-fetched pages.",
                           mFetchedUrls.Count,
                           reason,
                           mFetchedUrls.Count
                          );

        SafeInvokeOnEscalate();
        mDryRunAcc?.RecordNavigatorSwap(reason);
    }

    private void SafeInvokeOnEscalate()
    {
        if (mOnEscalate != null)
        {
            try
            {
                mOnEscalate.Invoke(mFetchedUrls.AsReadOnly());
            }
            catch(ObjectDisposedException ex)
            {
                mLogger.LogError(ex,
                                 "EscalationController onEscalate threw ObjectDisposedException; navigator swap committed but {Count} URLs may not have been requeued",
                                 mFetchedUrls.Count
                                );
            }
            catch(InvalidOperationException ex)
            {
                mLogger.LogError(ex,
                                 "EscalationController onEscalate threw InvalidOperationException; navigator swap committed but {Count} URLs may not have been requeued",
                                 mFetchedUrls.Count
                                );
            }
        }
    }

    /// <summary>
    ///     Pure function: returns a human-readable signal description if
    ///     <paramref name="html" /> contains a known SPA framework marker,
    ///     null otherwise. Substring containment only — no regex, no DOM
    ///     parsing. The first matching marker in priority order wins.
    /// </summary>
    private static string? DetectShellSignal(string html)
    {
        string? result = smShellMarkers
                         .Where(m => html.Contains(m.Marker, StringComparison.Ordinal))
                         .Select(m => m.Description)
                         .FirstOrDefault();
        return result;
    }

    private static readonly (string Marker, string Description)[] smShellMarkers =
        [
            (BlazorWasmMarker, BlazorWasmReason),
            (BlazorFrameworkMarker, BlazorFrameworkReason),
            (BlazorBootMarker, BlazorBootReason),
            (NextDataMarker, NextDataReason),
            (NuxtMarker, NuxtReason),
            (SvelteKitMarker, SvelteKitReason),
            (ReactRootMarker, ReactRootReason),
            (VueAppMarker, VueAppReason),
            (AngularVersionMarker, AngularVersionReason),
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
