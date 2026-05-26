// EscalationControllerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Crawling;

public sealed class EscalationControllerTests
{
    private sealed class StubNavigator : IPageNavigator
    {
        public Task<NavigationResult> NavigateAsync(IPage page, string url, CancellationToken ct)
            => throw new NotSupportedException("StubNavigator is for identity comparisons only");
    }

    private static ScrapeJob BuildJob(string? waitForSelector = null) =>
        new()
            {
                RootUrl = "https://example.com/docs",
                LibraryId = "test",
                Version = "1.0",
                LibraryHint = "test",
                AllowedUrlPatterns = ["example\\.com"],
                WaitForSelector = waitForSelector
            };

    [Fact]
    public void WaitForSelectorSuppliedEscalatesImmediately()
    {
        var ssr = new StubNavigator();
        var spa = new StubNavigator();
        var job = BuildJob(waitForSelector: "#app-main");

        var controller = new EscalationController(job,
                                                  ssr,
                                                  spa,
                                                  onEscalate: null,
                                                  dryRunAcc: null,
                                                  NullLogger<EscalationController>.Instance
                                                 );

        Assert.Same(ssr, controller.Active);
        controller.ObservePage("https://example.com/docs/intro", "<html><body>plain ssr content</body></html>");

        Assert.True(controller.ShouldEscalate);
        Assert.Same(spa, controller.Active);
    }

    [Fact]
    public void EscalationRequeuesAlreadyFetchedUrls()
    {
        var ssr = new StubNavigator();
        var spa = new StubNavigator();
        var job = BuildJob();

        var captured = new List<string>();
        Action<IReadOnlyList<string>> onEscalate = urls => captured.AddRange(urls);

        var controller = new EscalationController(job,
                                                  ssr,
                                                  spa,
                                                  onEscalate,
                                                  dryRunAcc: null,
                                                  NullLogger<EscalationController>.Instance
                                                 );

        controller.ObservePage("https://example.com/docs/a", "<html><body>plain content</body></html>");
        controller.ObservePage("https://example.com/docs/b", "<html><body>more content</body></html>");
        Assert.False(controller.ShouldEscalate);

        controller.ObservePage("https://example.com/docs/c",
                               "<html><body><script src=\"_framework/blazor.webassembly.js\"></script></body></html>"
                              );

        Assert.True(controller.ShouldEscalate);
        Assert.Equal(expected: 3, captured.Count);
        Assert.Contains("https://example.com/docs/a", captured);
        Assert.Contains("https://example.com/docs/b", captured);
        Assert.Contains("https://example.com/docs/c", captured);
    }

    [Fact]
    public void SsrHtmlPastWindowNeverEscalates()
    {
        var ssr = new StubNavigator();
        var spa = new StubNavigator();
        var job = BuildJob();

        var controller = new EscalationController(job,
                                                  ssr,
                                                  spa,
                                                  onEscalate: null,
                                                  dryRunAcc: null,
                                                  NullLogger<EscalationController>.Instance
                                                 );

        for(var i = 0; i < 4; i++)
            controller.ObservePage($"https://example.com/docs/{i}",
                                   "<!doctype html><html><body><main>real content</main></body></html>"
                                  );

        Assert.False(controller.ShouldEscalate);
        Assert.Same(ssr, controller.Active);
    }

    [Fact]
    public void EscalationNotifiesDryRunAccumulator()
    {
        var ssr = new StubNavigator();
        var spa = new StubNavigator();
        var job = BuildJob();
        var acc = new DryRunAccumulator();

        var controller = new EscalationController(job,
                                                  ssr,
                                                  spa,
                                                  onEscalate: null,
                                                  acc,
                                                  NullLogger<EscalationController>.Instance
                                                 );

        controller.ObservePage("https://example.com/docs/x",
                               "<html><body><div data-reactroot></div></body></html>"
                              );

        var snap = acc.Snapshot();
        Assert.NotNull(snap.Escalation);
        var escalation = snap.Escalation;
        Assert.NotNull(escalation);
        Assert.Equal(SpaFramework.ReactCsr, escalation.Framework);
    }

    [Fact]
    public void HtmlEscapedBlazorScriptInPreCodeDoesNotEscalate()
    {
        var ssr = new StubNavigator();
        var spa = new StubNavigator();
        var job = BuildJob();

        var controller = new EscalationController(job,
                                                  ssr,
                                                  spa,
                                                  onEscalate: null,
                                                  dryRunAcc: null,
                                                  NullLogger<EscalationController>.Instance
                                                 );

        const string tutorialHtml = "<!doctype html><html><body><h1>Setup</h1>" +
                                    "<p>Add this script tag:</p>" +
                                    "<pre><code class=\"lang-razor\">" +
                                    "&lt;script src=&quot;_framework/blazor.server.js&quot;&gt;&lt;/script&gt;" +
                                    "</code></pre>" +
                                    "<p>And this one:</p>" +
                                    "<pre><code>&lt;script src=&quot;blazor.webassembly.js&quot;&gt;&lt;/script&gt;</code></pre>" +
                                    "</body></html>";

        controller.ObservePage("https://example.com/docs/setup", tutorialHtml);

        Assert.False(controller.ShouldEscalate);
        Assert.Same(ssr, controller.Active);
    }

    [Fact]
    public void InlineCodeFrameworkMarkerDoesNotEscalate()
    {
        var ssr = new StubNavigator();
        var spa = new StubNavigator();
        var job = BuildJob();

        var controller = new EscalationController(job,
                                                  ssr,
                                                  spa,
                                                  onEscalate: null,
                                                  dryRunAcc: null,
                                                  NullLogger<EscalationController>.Instance
                                                 );

        const string proseWithInlineCode = "<!doctype html><html><body>" +
                                           "<p>Reference the <code>_framework/blazor.webassembly.js</code> file in your host page.</p>" +
                                           "</body></html>";

        controller.ObservePage("https://example.com/docs/intro", proseWithInlineCode);

        Assert.False(controller.ShouldEscalate);
        Assert.Same(ssr, controller.Active);
    }

    [Fact]
    public void RealBlazorScriptSrcOutsideCodeBlockStillEscalates()
    {
        var ssr = new StubNavigator();
        var spa = new StubNavigator();
        var job = BuildJob();

        var controller = new EscalationController(job,
                                                  ssr,
                                                  spa,
                                                  onEscalate: null,
                                                  dryRunAcc: null,
                                                  NullLogger<EscalationController>.Instance
                                                 );

        const string realBlazorShell = "<!doctype html><html><head>" +
                                       "<script src=\"_framework/blazor.webassembly.js\"></script>" +
                                       "</head><body><div id=\"app\">Loading...</div></body></html>";

        controller.ObservePage("https://example.com/app", realBlazorShell);

        Assert.True(controller.ShouldEscalate);
        Assert.Same(spa, controller.Active);
    }

    [Fact]
    public void AfterEscalateFurtherObserveIsNoOp()
    {
        var ssr = new StubNavigator();
        var spa = new StubNavigator();
        var job = BuildJob(waitForSelector: "#main");

        var callbackCount = 0;
        Action<IReadOnlyList<string>> onEscalate = _ => callbackCount++;

        var controller = new EscalationController(job,
                                                  ssr,
                                                  spa,
                                                  onEscalate,
                                                  dryRunAcc: null,
                                                  NullLogger<EscalationController>.Instance
                                                 );

        controller.ObservePage("https://example.com/docs/a", "<html></html>");
        controller.ObservePage("https://example.com/docs/b", "<html></html>");
        controller.ObservePage("https://example.com/docs/c", "<html></html>");

        Assert.Equal(expected: 1, callbackCount);
        Assert.True(controller.ShouldEscalate);
    }
}
