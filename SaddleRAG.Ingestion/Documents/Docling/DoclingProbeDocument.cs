// DoclingProbeDocument.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>
///     SaddleRAG-owned document used only to verify the external conversion boundary.
/// </summary>
public static class DoclingProbeDocument
{
    public static DoclingFile CreatePdf()
    {
        var bytes = Convert.FromBase64String(PdfBase64);
        return new DoclingFile(FileName, MediaType, bytes);
    }

    internal static bool ContainsMarker(DoclingMappedDocument? document)
    {
        var result = document != null
                     && (ContainsMarker(document.MarkdownContent)
                         || ContainsMarker(document.TextContent));
        return result;
    }

    private static bool ContainsMarker(string content)
    {
        var result = content.Contains(Marker, StringComparison.Ordinal)
                     || content.Contains(MarkdownEscapedMarker, StringComparison.Ordinal);
        return result;
    }

    public const string Marker = "SADDLERAG_DOCLING_PROBE_2026_08_04";

    private const string MarkdownEscapedMarker = @"SADDLERAG\_DOCLING\_PROBE\_2026\_08\_04";
    private const string FileName = "saddlerag-docling-probe.pdf";
    private const string MediaType = "application/pdf";
    private const string PdfBase64 =
        "JVBERi0xLjMKJZOMi54gUmVwb3J0TGFiIEdlbmVyYXRlZCBQREYgZG9jdW1lbnQgKG9wZW5zb3VyY2UpCjEgMCBvYmoKPDwKL0YxIDIgMCBSIC9GMiAzIDAgUgo+PgplbmRvYmoKMiAwIG9iago8PAovQmFzZUZvbnQgL0hlbHZldGljYSAvRW5jb2RpbmcgL1dpbkFuc2lFbmNvZGluZyAvTmFtZSAvRjEgL1N1YnR5cGUgL1R5cGUxIC9UeXBlIC9Gb250Cj4+CmVuZG9iagozIDAgb2JqCjw8Ci9CYXNlRm9udCAvSGVsdmV0aWNhLUJvbGQgL0VuY29kaW5nIC9XaW5BbnNpRW5jb2RpbmcgL05hbWUgL0YyIC9TdWJ0eXBlIC9UeXBlMSAvVHlwZSAvRm9udAo+PgplbmRvYmoKNCAwIG9iago8PAovQ29udGVudHMgOCAwIFIgL01lZGlhQm94IFsgMCAwIDYxMiA3OTIgXSAvUGFyZW50IDcgMCBSIC9SZXNvdXJjZXMgPDwKL0ZvbnQgMSAwIFIgL1Byb2NTZXQgWyAvUERGIC9UZXh0IC9JbWFnZUIgL0ltYWdlQyAvSW1hZ2VJIF0KPj4gL1JvdGF0ZSAwIC9UcmFucyA8PAoKPj4gCiAgL1R5cGUgL1BhZ2UKPj4KZW5kb2JqCjUgMCBvYmoKPDwKL1BhZ2VNb2RlIC9Vc2VOb25lIC9QYWdlcyA3IDAgUiAvVHlwZSAvQ2F0YWxvZwo+PgplbmRvYmoKNiAwIG9iago8PAovQXV0aG9yIChhbm9ueW1vdXMpIC9DcmVhdGlvbkRhdGUgKEQ6MjAwMDAxMDEwMDAwMDArMDAnMDAnKSAvQ3JlYXRvciAoYW5vbnltb3VzKSAvS2V5d29yZHMgKCkgL01vZERhdGUgKEQ6MjAwMDAxMDEwMDAwMDArMDAnMDAnKSAvUHJvZHVjZXIgKFJlcG9ydExhYiBQREYgTGlicmFyeSAtIFwob3BlbnNvdXJjZVwpKSAKICAvU3ViamVjdCAodW5zcGVjaWZpZWQpIC9UaXRsZSAodW50aXRsZWQpIC9UcmFwcGVkIC9GYWxzZQo+PgplbmRvYmoKNyAwIG9iago8PAovQ291bnQgMSAvS2lkcyBbIDQgMCBSIF0gL1R5cGUgL1BhZ2VzCj4+CmVuZG9iago4IDAgb2JqCjw8Ci9MZW5ndGggNTc2Cj4+CnN0cmVhbQoxIDAgMCAxIDAgMCBjbSAgQlQgL0YxIDEyIFRmIDE0LjQgVEwgRVQKQlQgL0YyIDE2IFRmIDE5LjIgVEwgRVQKLjE4MDM5MiAuNDU0OTAyIC43MDk4MDQgcmcKQlQgMSAwIDAgMSA3MiA3MjAgVG0gKFNhZGRsZVJBRyBEb2N1bWVudCBDb252ZXJzaW9uIFByb2JlKSBUaiBUKiBFVAowIDAgMCByZwpCVCAvRjIgMTEgVGYgMTMuMiBUTCBFVApCVCAxIDAgMCAxIDcyIDY4Ny42IFRtIChTQURETEVSQUdfRE9DTElOR19QUk9CRV8yMDI2XzA4XzA0KSBUaiBUKiBFVAouMTgwMzkyIC40NTQ5MDIgLjcwOTgwNCByZwpCVCAvRjIgMTMgVGYgMTUuNiBUTCBFVApCVCAxIDAgMCAxIDcyIDY1MS42IFRtIChQdXJwb3NlKSBUaiBUKiBFVAowIDAgMCByZwpCVCAvRjEgMTEgVGYgMTMuMiBUTCBFVApCVCAxIDAgMCAxIDcyIDYzMCBUbSAxNSBUTCAoVGhpcyBTYWRkbGVSQUctb3duZWQgdGVzdCBkb2N1bWVudCB2ZXJpZmllcyB0aGF0IGEgdXNlci1tYW5hZ2VkIERvY2xpbmcgZW5kcG9pbnQgY2FuKSBUaiBUKiAoY29udmVydCBQREYgYW5kIERPQ1ggaW5wdXQgd2hpbGUgcHJlc2VydmluZyBrbm93biB0ZXh0LikgVGogVCogRVQKIAplbmRzdHJlYW0KZW5kb2JqCnhyZWYKMCA5CjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAwMDAwMDA2MSAwMDAwMCBuIAowMDAwMDAwMTAyIDAwMDAwIG4gCjAwMDAwMDAyMDkgMDAwMDAgbiAKMDAwMDAwMDMyMSAwMDAwMCBuIAowMDAwMDAwNTE0IDAwMDAwIG4gCjAwMDAwMDA1ODIgMDAwMDAgbiAKMDAwMDAwMDg0MyAwMDAwMCBuIAowMDAwMDAwOTAyIDAwMDAwIG4gCnRyYWlsZXIKPDwKL0lEIApbPDFjMTc4MTk4ZmJkZmE1MWIyNTk5NWQ4OWQ0MTAyMDQzPjwxYzE3ODE5OGZiZGZhNTFiMjU5OTVkODlkNDEwMjA0Mz5dCiUgUmVwb3J0TGFiIGdlbmVyYXRlZCBQREYgZG9jdW1lbnQgLS0gZGlnZXN0IChvcGVuc291cmNlKQoKL0luZm8gNiAwIFIKL1Jvb3QgNSAwIFIKL1NpemUgOQo+PgpzdGFydHhyZWYKMTUyOAolJUVPRgo=";
}
