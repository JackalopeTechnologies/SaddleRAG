// CrawlQueueTestSite.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SaddleRAG.Tests.Crawling;

/// <summary>Serves a repeated documentation sidebar on an ephemeral loopback port.</summary>
internal sealed class CrawlQueueTestSite : IAsyncDisposable
{
    private CrawlQueueTestSite(WebApplication application, string origin)
    {
        mApplication = application;
        Origin = origin;
    }

    private readonly WebApplication mApplication;
    public string Origin { get; }
    public string Url(string path) => Origin + "/docs/" + path;

    public static async Task<CrawlQueueTestSite> StartAsync(IEnumerable<string> links,
        Func<string, IEnumerable<string>>? linksForPath = null,
        Func<HttpContext, Task>? beforeResponse = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Development"
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        WebApplication application = builder.Build();
        application.Run(async context =>
        {
            if (beforeResponse != null)
                await beforeResponse(context);
            IEnumerable<string> pageLinks = linksForPath?.Invoke(context.Request.Path.Value ?? "") ?? links;
            string navigation = string.Join("", pageLinks.Select(link => $"<a href='/docs/{link}'>{link}</a>"));
            string html = $"<html><head><title>Queue test</title></head><body><nav>{navigation}</nav>" +
                          $"<main>{new string('x', 4096)}</main></body></html>";
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(html, context.RequestAborted);
        });
        await application.StartAsync(TestContext.Current.CancellationToken);
        string origin = application.Services.GetRequiredService<IServer>()
                                   .Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                        ?? throw new InvalidOperationException("The queue fixture has no address.");
        return new CrawlQueueTestSite(application, origin);
    }

    public async ValueTask DisposeAsync()
    {
        await mApplication.StopAsync(TestContext.Current.CancellationToken);
        await mApplication.DisposeAsync();
    }
}
