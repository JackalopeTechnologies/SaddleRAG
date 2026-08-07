// TestDoclingConnection.js
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.
//
// Read-only compatibility probe for the user-managed endpoint. The probe waits
// through a bounded cold start, requires /health and /ready status "ok", then
// submits a tiny SaddleRAG-owned PDF to /v1/convert/file. It never changes the
// external process or the user's startup configuration.

var _endpoint = _trimEndpoint(Session.Property("DOCLINGENDPOINT"));
var _healthUrl = _endpoint + "/health";
var _readyUrl = _endpoint + "/ready";
var _conversionUrl = _endpoint + "/v1/convert/file";
var _startupGraceSeconds = 120;
var _pollMilliseconds = 1000;
var _healthRequestMilliseconds = 10000;
var _readyRequestMilliseconds = 30000;
var _conversionRequestMilliseconds = 120000;
var _deadline = new Date().getTime() + (_startupGraceSeconds * 1000);
var _finished = false;
var _lastTransientCode = "DOCLING_ENDPOINT_UNREACHABLE";
var _lastTransientDetail = "The configured endpoint could not be reached.";

Session.Property("DOCLINGHEALTHURL") = _healthUrl;

if (!_isHttpEndpoint(_endpoint)) {
    _setStatus("DOCLING_HEALTH_INVALID", "Enter an absolute HTTP or HTTPS endpoint without a query or fragment.");
    _finished = true;
}

while (!_finished && new Date().getTime() <= _deadline) {
    var _health = _request("GET", _healthUrl, _healthRequestMilliseconds, null, null);
    if (_health.error) {
        _lastTransientCode = _health.timedOut ? "DOCLING_HEALTH_TIMEOUT" : "DOCLING_ENDPOINT_UNREACHABLE";
        _lastTransientDetail = _health.error;
    } else if (_health.status === 401 || _health.status === 403) {
        _setStatus("DOCLING_UNAUTHORIZED", "The health endpoint rejected the request.");
        _finished = true;
    } else if (_health.status === 404 || _health.status === 405) {
        _setStatus("DOCLING_API_INCOMPATIBLE", "The configured endpoint does not expose the expected /health route.");
        _finished = true;
    } else if (_health.status !== 200 || !_hasOkStatus(_health.text)) {
        _setStatus("DOCLING_HEALTH_INVALID", "The /health response was not HTTP 200 with status ok.");
        _finished = true;
    } else {
        var _ready = _request("GET", _readyUrl, _readyRequestMilliseconds, null, null);
        if (_ready.error) {
            _lastTransientCode = _ready.timedOut ? "DOCLING_HEALTH_TIMEOUT" : "DOCLING_ENDPOINT_UNREACHABLE";
            _lastTransientDetail = _ready.error;
        } else if (_ready.status === 401 || _ready.status === 403) {
            _setStatus("DOCLING_UNAUTHORIZED", "The readiness endpoint rejected the request.");
            _finished = true;
        } else if (_ready.status === 404 || _ready.status === 405) {
            _setStatus("DOCLING_API_INCOMPATIBLE", "The configured endpoint does not expose the expected /ready route.");
            _finished = true;
        } else if (_ready.status === 503) {
            _lastTransientCode = "DOCLING_MODELS_UNAVAILABLE";
            _lastTransientDetail = "The endpoint is healthy but its models are not ready.";
        } else if (_ready.status !== 200 || !_hasOkStatus(_ready.text)) {
            _setStatus("DOCLING_API_INCOMPATIBLE", "The /ready response was not HTTP 200 with status ok.");
            _finished = true;
        } else {
            _runConversionProbe();
            _finished = true;
        }
    }

    if (!_finished && new Date().getTime() <= _deadline) {
        _waitMilliseconds(_pollMilliseconds);
    }
}

if (!_finished) {
    _setStatus(_lastTransientCode, _lastTransientDetail);
}

function _runConversionProbe()
{
    var _boundary = "----------------SaddleRAGDoclingProbe20260804";
    var _body = _buildMultipartBody(_boundary);
    var _conversion = _request("POST",
                               _conversionUrl,
                               _conversionRequestMilliseconds,
                               _body,
                               "multipart/form-data; boundary=" + _boundary);
    if (_conversion.error) {
        var _code = _conversion.timedOut ? "DOCLING_CONVERSION_FAILED" : "DOCLING_ENDPOINT_UNREACHABLE";
        _setStatus(_code, _conversion.error);
    } else if (_conversion.status === 401 || _conversion.status === 403) {
        _setStatus("DOCLING_UNAUTHORIZED", "The conversion endpoint rejected the request.");
    } else if (_conversion.status === 404 || _conversion.status === 405) {
        _setStatus("DOCLING_API_INCOMPATIBLE", "The endpoint does not expose the expected conversion route.");
    } else if (_conversion.status === 503) {
        _setStatus("DOCLING_MODELS_UNAVAILABLE", "The conversion endpoint reports unavailable models.");
    } else if (_conversion.status < 200 || _conversion.status >= 300) {
        _setStatus("DOCLING_CONVERSION_FAILED", "Conversion returned HTTP " + _conversion.status + ".");
    } else if (!_containsProbeMarker(_conversion.text)) {
        _setStatus("DOCLING_CONVERSION_FAILED", "Conversion succeeded but omitted the owned verification marker.");
    } else {
        _setStatus("DOCLING_READY", "Health, model readiness, and the SaddleRAG-owned conversion probe passed.");
    }
}

function _request(method, url, timeoutMilliseconds, body, contentType)
{
    var _result = { status: 0, text: "", error: "", timedOut: false };
    try {
        var _http = new ActiveXObject("MSXML2.ServerXMLHTTP.6.0");
        _http.open(method, url, false);
        _http.setTimeouts(5000, 5000, timeoutMilliseconds, timeoutMilliseconds);
        if (contentType) {
            _http.setRequestHeader("Content-Type", contentType);
        }
        _http.send(body);
        _result.status = _http.status;
        _result.text = _http.responseText || "";
    } catch (_error) {
        var _message = (_error && _error.message) ? _error.message : "The endpoint request failed.";
        _result.error = _singleLine(_message);
        _result.timedOut = _result.error.toLowerCase().indexOf("timeout") >= 0
                           || _result.error.toLowerCase().indexOf("timed out") >= 0;
    }
    return _result;
}

function _buildMultipartBody(boundary)
{
    var _pdf = _buildProbePdf();
    var _body = "--" + boundary + "\r\n"
              + "Content-Disposition: form-data; name=\"files\"; filename=\"saddlerag-docling-probe.pdf\"\r\n"
              + "Content-Type: application/pdf\r\n\r\n"
              + _pdf + "\r\n";
    _body += _formField(boundary, "from_formats", "pdf");
    _body += _formField(boundary, "to_formats", "md");
    _body += _formField(boundary, "to_formats", "json");
    _body += _formField(boundary, "to_formats", "text");
    _body += _formField(boundary, "do_ocr", "true");
    _body += _formField(boundary, "table_mode", "accurate");
    _body += _formField(boundary, "pipeline", "standard");
    _body += _formField(boundary, "image_export_mode", "placeholder");
    _body += _formField(boundary, "do_picture_description", "false");
    _body += _formField(boundary, "do_picture_classification", "false");
    _body += _formField(boundary, "do_code_enrichment", "false");
    _body += _formField(boundary, "do_formula_enrichment", "false");
    _body += _formField(boundary, "abort_on_error", "false");
    _body += "--" + boundary + "--\r\n";
    return _utf8Bytes(_body);
}

function _formField(boundary, name, value)
{
    return "--" + boundary + "\r\n"
           + "Content-Disposition: form-data; name=\"" + name + "\"\r\n\r\n"
           + value + "\r\n";
}

function _buildProbePdf()
{
    var _marker = "SADDLERAG_DOCLING_PROBE_2026_08_04";
    var _content = "BT /F1 12 Tf 72 720 Td (" + _marker + ") Tj ET";
    var _objects = [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /Length " + _content.length + " >>\nstream\n" + _content + "\nendstream"
    ];
    var _pdf = "%PDF-1.4\n% SaddleRAG-owned Docling compatibility probe\n";
    var _offsets = [];
    var _index;
    for (_index = 0; _index < _objects.length; _index++) {
        _offsets.push(_pdf.length);
        _pdf += (_index + 1) + " 0 obj\n" + _objects[_index] + "\nendobj\n";
    }
    var _xrefOffset = _pdf.length;
    _pdf += "xref\n0 6\n0000000000 65535 f \n";
    for (_index = 0; _index < _offsets.length; _index++) {
        _pdf += _padOffset(_offsets[_index]) + " 00000 n \n";
    }
    _pdf += "trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n" + _xrefOffset + "\n%%EOF\n";
    return _pdf;
}

function _utf8Bytes(value)
{
    var _stream = new ActiveXObject("ADODB.Stream");
    _stream.Type = 2;
    _stream.Charset = "utf-8";
    _stream.Open();
    _stream.WriteText(value);
    _stream.Position = 0;
    _stream.Type = 1;
    _stream.Position = 3;
    var _bytes = _stream.Read();
    _stream.Close();
    return _bytes;
}

function _padOffset(value)
{
    var _result = String(value);
    while (_result.length < 10) {
        _result = "0" + _result;
    }
    return _result;
}

function _containsProbeMarker(value)
{
    var _raw = "SADDLERAG_DOCLING_PROBE_2026_08_04";
    var _escaped = "SADDLERAG\\_DOCLING\\_PROBE\\_2026\\_08\\_04";
    return value.indexOf(_raw) >= 0 || value.indexOf(_escaped) >= 0;
}

function _hasOkStatus(value)
{
    return /\"status\"\s*:\s*\"ok\"/i.test(value);
}

function _trimEndpoint(value)
{
    var _result = value ? String(value).replace(/^\s+|\s+$/g, "") : "";
    _result = _result.replace(/\/+$/g, "");
    return _result;
}

function _isHttpEndpoint(value)
{
    return /^https?:\/\/[^?#]+$/i.test(value);
}

function _waitMilliseconds(milliseconds)
{
    var _until = new Date().getTime() + milliseconds;
    while (new Date().getTime() < _until) {
    }
}

function _singleLine(value)
{
    var _result = String(value).replace(/[\r\n\t]+/g, " ");
    if (_result.length > 240) {
        _result = _result.substring(0, 237) + "...";
    }
    return _result;
}

function _setStatus(reasonCode, detail)
{
    Session.Property("DOCLINGSTATUS") = reasonCode + ": " + _singleLine(detail);
}
