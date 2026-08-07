// LoopbackDocumentSite.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.
// Stage 8 acceptance contract.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SaddleRAG.Tests.Crawling;

/// <summary>
///     Owned Stage 8 HTTP fixture. It binds only to loopback on an operating-
///     system-assigned port and serves deterministic bytes without external I/O.
/// </summary>
internal sealed class LoopbackDocumentSite : IAsyncDisposable
{
    private LoopbackDocumentSite(TcpListener listener)
    {
        mListener = listener;
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        BaseUri = new Uri($"http://127.0.0.1:{endpoint.Port}/", UriKind.Absolute);
        mAcceptLoop = AcceptLoopAsync(mCancellation.Token);
    }

    public Uri BaseUri { get; }

    public string MixedIndexUrl => Url("mixed/index.html");

    public string ManualPdfUrl => Url("manual.pdf");

    public string ManualDocxUrl => Url("manual.docx");

    public string ExtensionlessPdfUrl => Url("download?id=17");

    public string MisleadingPdfUrl => Url("not-a-document.pdf");

    public string RedirectUrl => Url("redirected-manual");

    public string BrokenIndexUrl => Url("broken/index.html");

    public string BrokenPdfUrl => Url("broken.pdf");

    public string HtmlOnlyIndexUrl => Url("html-only/index.html");

    public byte[] PdfBody { get; } =
        [
            0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x0A,
            0x00, 0xFF, 0x01, 0x02, 0x0A,
            0x53, 0x41, 0x44, 0x44, 0x4C, 0x45, 0x52, 0x41, 0x47, 0x5F, 0x57, 0x45, 0x42, 0x5F, 0x50, 0x44, 0x46
        ];

    public byte[] DocxBody { get; } =
        [
            0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00,
            0x53, 0x41, 0x44, 0x44, 0x4C, 0x45, 0x52, 0x41, 0x47, 0x5F, 0x57, 0x45, 0x42, 0x5F, 0x44, 0x4F, 0x43, 0x58
        ];

    public byte[] ExtensionlessPdfBody { get; } =
        "%PDF-1.7\nSADDLERAG_WEB_EXTENSIONLESS_PDF\n%%EOF"u8.ToArray();

    public byte[] BrokenPdfBody { get; } =
        "%PDF-1.7\nSADDLERAG_WEB_BROKEN_PDF\n%%EOF"u8.ToArray();

    private readonly CancellationTokenSource mCancellation = new();
    private readonly Task mAcceptLoop;
    private readonly TcpListener mListener;

    public static LoopbackDocumentSite Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        return new LoopbackDocumentSite(listener);
    }

    public async ValueTask DisposeAsync()
    {
        await mCancellation.CancelAsync();
        mListener.Stop();
        try
        {
            await mAcceptLoop;
        }
        catch(OperationCanceledException)
        {
            // Cancellation is the normal fixture shutdown path.
        }
        catch(SocketException) when(mCancellation.IsCancellationRequested)
        {
            // Stopping TcpListener releases a pending accept on Windows.
        }
        finally
        {
            mCancellation.Dispose();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using TcpClient client = await mListener.AcceptTcpClientAsync(ct);
            await HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        await using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream,
                                            Encoding.ASCII,
                                            detectEncodingFromByteOrderMarks: false,
                                            leaveOpen: true);
        string? requestLine = await reader.ReadLineAsync(ct);
        string? header;
        do
        {
            header = await reader.ReadLineAsync(ct);
        } while (!string.IsNullOrEmpty(header));

        string target = RequestTarget(requestLine);
        RouteResponse response = Route(target);
        byte[] headers = ResponseHeaders(response);
        await stream.WriteAsync(headers, ct);
        if (response.Body.Length > 0)
            await stream.WriteAsync(response.Body, ct);
        await stream.FlushAsync(ct);
    }

    private RouteResponse Route(string target)
    {
        string path = new Uri(BaseUri, target).AbsolutePath;
        RouteResponse result = path switch
            {
                "/mixed/index.html" => Html(MixedIndexHtml),
                "/manual.pdf" => Binary("application/pdf",
                                         PdfBody,
                                         new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                             {
                                                 ["etag"] = "\"owned-pdf-v1\"",
                                                 ["last-modified"] = "Tue, 04 Aug 2026 18:00:00 GMT"
                                             }),
                "/manual.docx" => Binary(DocxMediaType, DocxBody),
                "/download" => Binary("application/pdf", ExtensionlessPdfBody),
                "/not-a-document.pdf" => Html(MisleadingHtml),
                "/redirected-manual" => Redirect("/manual.pdf"),
                "/broken/index.html" => Html(BrokenIndexHtml),
                "/broken.pdf" => Binary("application/pdf", BrokenPdfBody),
                "/html-only/index.html" => Html(HtmlOnlyIndex),
                "/html-only/page.html" => Html(HtmlOnlyPage),
                _ => NotFound()
            };
        return result;
    }

    private static RouteResponse Html(string value) =>
        new(200,
            "OK",
            "text/html; charset=utf-8",
            Encoding.UTF8.GetBytes(value),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static RouteResponse Binary(string contentType,
                                        byte[] body,
                                        IReadOnlyDictionary<string, string>? headers = null) =>
        new(200,
            "OK",
            contentType,
            body,
            headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static RouteResponse Redirect(string location) =>
        new(302,
            "Found",
            "text/plain",
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["location"] = location
                });

    private static RouteResponse NotFound() =>
        new(404,
            "Not Found",
            "text/plain; charset=utf-8",
            "not found"u8.ToArray(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static string RequestTarget(string? requestLine)
    {
        string result = "/";
        if (!string.IsNullOrWhiteSpace(requestLine))
        {
            string[] pieces = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length >= RequestTargetPieceCount)
                result = pieces[1];
        }

        return result;
    }

    private static byte[] ResponseHeaders(RouteResponse response)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ")
               .Append(response.StatusCode)
               .Append(' ')
               .Append(response.StatusText)
               .Append("\r\nContent-Type: ")
               .Append(response.ContentType)
               .Append("\r\nContent-Length: ")
               .Append(response.Body.Length)
               .Append("\r\nConnection: close\r\n");
        foreach(KeyValuePair<string, string> header in response.Headers)
            builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        builder.Append("\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private string Url(string relative) => new Uri(BaseUri, relative).AbsoluteUri;

    private sealed record RouteResponse(int StatusCode,
                                        string StatusText,
                                        string ContentType,
                                        byte[] Body,
                                        IReadOnlyDictionary<string, string> Headers);

    private const int RequestTargetPieceCount = 2;
    private const string DocxMediaType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private const string MixedIndexHtml = """
                                                  <!doctype html>
                                                  <html><head><title>Mixed owned site</title></head>
                                                  <body>
                                                    <main>SADDLERAG_WEB_HTML_ROOT</main>
                                                    <a href="/manual.pdf">PDF</a>
                                                    <a href="/manual.docx">DOCX</a>
                                                    <a href="/download?id=17">Extensionless PDF</a>
                                                    <a href="/not-a-document.pdf">Misleading HTML</a>
                                                  </body></html>
                                                  """;

    private const string MisleadingHtml = """
                                                  <!doctype html>
                                                  <html><head><title>Actually HTML</title></head>
                                                  <body><main>SADDLERAG_WEB_MISLEADING_HTML</main></body></html>
                                                  """;

    private const string BrokenIndexHtml = """
                                                  <!doctype html>
                                                  <html><head><title>Broken document site</title></head>
                                                  <body>
                                                    <main>SADDLERAG_WEB_BEFORE_BROKEN_DOCUMENT</main>
                                                    <a href="/broken.pdf">Broken supported PDF</a>
                                                  </body></html>
                                                  """;

    private const string HtmlOnlyIndex = """
                                                <!doctype html>
                                                <html><head><title>HTML only</title></head>
                                                <body>
                                                  <main>SADDLERAG_HTML_ONLY_ROOT</main>
                                                  <a href="/html-only/page.html">Second HTML page</a>
                                                </body></html>
                                                """;

    private const string HtmlOnlyPage = """
                                               <!doctype html>
                                               <html><head><title>HTML child</title></head>
                                               <body><main>SADDLERAG_HTML_ONLY_CHILD</main></body></html>
                                               """;
}
