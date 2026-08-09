// TestDoclingConnection.js
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.
//
// Read-only compatibility probe for the user-managed endpoint. The probe waits
// through a bounded cold start, requires /health and /ready status "ok", then
// submits a tiny SaddleRAG-owned PDF through Docling's asynchronous conversion
// contract. It never changes the external process or the user's startup
// configuration.

var _endpoint = _trimEndpoint(Session.Property("DOCLINGENDPOINT"));
var _healthUrl = _endpoint + "/health";
var _readyUrl = _endpoint + "/ready";
var _conversionUrl = _endpoint + "/v1/convert/file/async";
var _statusUrl = _endpoint + "/v1/status/poll/";
var _resultUrl = _endpoint + "/v1/result/";
var _startupGraceSeconds = 120;
var _pollMilliseconds = 1000;
var _healthRequestMilliseconds = 10000;
var _readyRequestMilliseconds = 30000;
var _conversionRequestMilliseconds = 30000;
var _conversionPollMilliseconds = 5000;
// MSI JScript custom actions have no cancellation callback. This deadline and
// the per-request phase budgets bound the complete conversion probe instead.
var _conversionTotalMilliseconds = 600000;
var _deadline = new Date().getTime() + (_startupGraceSeconds * 1000);
var _finished = false;
var _lastTransientCode = "DOCLING_ENDPOINT_UNREACHABLE";
var _lastTransientDetail = "The configured endpoint could not be reached.";

Session.Property("DOCLINGHEALTHURL") = _healthUrl;

if (!_isHttpEndpoint(_endpoint)) {
    _setStatus("DOCLING_HEALTH_INVALID", "Enter an absolute HTTP or HTTPS endpoint without embedded credentials, query, or fragment.");
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
    var _conversionDeadline = new Date().getTime() + _conversionTotalMilliseconds;
    var _submission = _boundedRequest("POST",
                                      _conversionUrl,
                                      _conversionDeadline,
                                      _body,
                                      "multipart/form-data; boundary=" + _boundary);
    if (_setConversionRequestFailure(_submission, "submission")) {
        return;
    }

    var _task = _readTaskStatus(_submission.text, null);
    if (!_task.valid) {
        _setStatus("DOCLING_API_INCOMPATIBLE", "Docling returned an invalid asynchronous submission status.");
        return;
    }

    while (_task.status === "pending" || _task.status === "started") {
        // Docling Serve 1.29 accepts wait=5 but may return immediately, so the
        // installer owns this delay while still honoring the total deadline.
        if (!_waitWithinDeadline(_conversionDeadline, _conversionPollMilliseconds)) {
            _setConversionTimeout();
            return;
        }

        var _poll = _boundedRequest("GET",
                                    _statusUrl + encodeURIComponent(_task.id) + "?wait=5",
                                    _conversionDeadline,
                                    null,
                                    null);
        // Completed tasks can leave the polling queue before their result is
        // visible. Treat that 404 as the same eventual-result window handled
        // by the bounded result retries.
        if (_poll.status === 404 && !_poll.error) {
            _readConversionResult(_task.id, _conversionDeadline);
            return;
        }
        if (_setConversionRequestFailure(_poll, "status poll")) {
            return;
        }

        _task = _readTaskStatus(_poll.text, _task.id);
        if (!_task.valid) {
            _setStatus("DOCLING_API_INCOMPATIBLE", "Docling returned an invalid asynchronous poll status.");
            return;
        }
    }

    if (_task.status === "failure") {
        _setStatus("DOCLING_CONVERSION_FAILED", "Docling reported that the owned conversion probe failed.");
    } else if (_task.status === "skipped") {
        _setStatus("DOCLING_CONVERSION_FAILED", "Docling skipped the owned conversion probe.");
    } else if (_task.status === "partial_success") {
        _setStatus("DOCLING_CONVERSION_FAILED", "Docling returned only partial success for the owned conversion probe.");
    } else if (_task.status === "success") {
        _readConversionResult(_task.id, _conversionDeadline);
    } else {
        _setStatus("DOCLING_API_INCOMPATIBLE", "Docling returned an unknown asynchronous task status.");
    }
}

function _readConversionResult(taskId, deadline)
{
    var _retryDelays = [2000, 4000, 8000];
    var _attempt = 0;
    while (true) {
        var _result = _boundedRequest("GET",
                                      _resultUrl + encodeURIComponent(taskId),
                                      deadline,
                                      null,
                                      null);
        if (_result.status === 404 && !_result.error && _attempt < _retryDelays.length) {
            if (!_waitWithinDeadline(deadline, _retryDelays[_attempt])) {
                _setConversionTimeout();
                return;
            }
            _attempt++;
        } else {
            if (_setConversionRequestFailure(_result, "result")) {
                return;
            }
            if (!_containsProbeMarker(_result.text)) {
                _setStatus("DOCLING_CONVERSION_FAILED", "Conversion succeeded but omitted the owned verification marker.");
            } else {
                _setStatus("DOCLING_READY", "Health, model readiness, and the SaddleRAG-owned conversion probe passed.");
            }
            return;
        }
    }
}

function _boundedRequest(method, url, deadline, body, contentType)
{
    var _remaining = deadline - new Date().getTime();
    if (_remaining <= 0) {
        return { status: 0, text: "", error: "The Docling conversion deadline expired.", timedOut: true };
    }
    var _timeout = Math.min(_conversionRequestMilliseconds, _remaining);
    var _phaseTimeout = Math.max(1, Math.floor(_timeout / 4));
    return _request(method, url, _phaseTimeout, body, contentType);
}

function _setConversionRequestFailure(result, phase)
{
    var _failed = true;
    if (result.error) {
        if (result.timedOut) {
            _setConversionTimeout();
        } else {
            _setStatus("DOCLING_ENDPOINT_UNREACHABLE", "The Docling " + phase + " request could not be completed.");
        }
    } else if (result.status === 401 || result.status === 403) {
        _setStatus("DOCLING_UNAUTHORIZED", "The Docling " + phase + " endpoint rejected the request.");
    } else if (result.status === 404 || result.status === 405 || result.status === 422) {
        _setStatus("DOCLING_API_INCOMPATIBLE", "The endpoint does not expose the expected asynchronous " + phase + " contract.");
    } else if (result.status === 503) {
        _setStatus("DOCLING_MODELS_UNAVAILABLE", "The Docling " + phase + " endpoint reports unavailable models.");
    } else if (result.status < 200 || result.status >= 300) {
        _setStatus("DOCLING_CONVERSION_FAILED", "The Docling " + phase + " request returned HTTP " + result.status + ".");
    } else {
        _failed = false;
    }
    return _failed;
}

function _setConversionTimeout()
{
    _setStatus("DOCLING_CONVERSION_TIMEOUT", "The owned conversion probe did not finish within 600000 milliseconds.");
}

function _waitWithinDeadline(deadline, milliseconds)
{
    var _remaining = deadline - new Date().getTime();
    var _canWait = _remaining >= milliseconds;
    if (_canWait) {
        _waitMilliseconds(milliseconds);
    }
    return _canWait;
}

function _readTaskStatus(value, expectedTaskId)
{
    var _fields = _readTopLevelTaskFields(value);
    var _knownStatus = _fields.taskStatus === "pending"
                       || _fields.taskStatus === "started"
                       || _fields.taskStatus === "failure"
                       || _fields.taskStatus === "success"
                       || _fields.taskStatus === "partial_success"
                       || _fields.taskStatus === "skipped";
    var _matchesId = expectedTaskId === null || _fields.taskId === expectedTaskId;
    var _valid = _fields.valid
                 && _fields.taskId.length > 0
                 && _fields.taskType === "convert"
                 && _knownStatus
                 && _matchesId;
    return { valid: _valid, id: _fields.taskId, status: _fields.taskStatus };
}

function _readTopLevelTaskFields(value)
{
    var _result = { valid: false, taskId: "", taskType: "", taskStatus: "" };
    var _seen = { task_id: false, task_type: false, task_status: false };
    var _index = _skipWhitespace(value, 0);
    if (value.charAt(_index) !== "{") {
        return _result;
    }
    _index = _skipWhitespace(value, _index + 1);
    while (_index < value.length && value.charAt(_index) !== "}") {
        var _key = _parseJsonString(value, _index);
        if (!_key.valid) {
            return _result;
        }
        _index = _skipWhitespace(value, _key.next);
        if (value.charAt(_index) !== ":") {
            return _result;
        }
        _index = _skipWhitespace(value, _index + 1);
        var _isTaskField = _key.value === "task_id"
                           || _key.value === "task_type"
                           || _key.value === "task_status";
        if (_isTaskField) {
            if (_seen[_key.value]) {
                return _result;
            }
            var _field = _parseJsonString(value, _index);
            if (!_field.valid) {
                return _result;
            }
            _seen[_key.value] = true;
            if (_key.value === "task_id") {
                _result.taskId = _field.value;
            } else if (_key.value === "task_type") {
                _result.taskType = _field.value;
            } else {
                _result.taskStatus = _field.value;
            }
            _index = _field.next;
        } else {
            _index = _skipJsonValue(value, _index);
            if (_index < 0) {
                return _result;
            }
        }
        _index = _skipWhitespace(value, _index);
        if (value.charAt(_index) === ",") {
            _index = _skipWhitespace(value, _index + 1);
            if (value.charAt(_index) === "}") {
                return _result;
            }
        } else if (value.charAt(_index) !== "}") {
            return _result;
        }
    }
    if (value.charAt(_index) !== "}") {
        return _result;
    }
    _index = _skipWhitespace(value, _index + 1);
    _result.valid = _index === value.length
                    && _seen.task_id
                    && _seen.task_type
                    && _seen.task_status;
    return _result;
}

function _parseJsonString(value, start)
{
    var _result = { valid: false, value: "", next: start };
    if (value.charAt(start) !== "\"") {
        return _result;
    }
    var _decoded = "";
    var _index = start + 1;
    while (_index < value.length) {
        var _character = value.charAt(_index);
        if (_character === "\"") {
            _result.valid = true;
            _result.value = _decoded;
            _result.next = _index + 1;
            return _result;
        }
        if (_character === "\\") {
            _index++;
            if (_index >= value.length) {
                return _result;
            }
            var _escape = value.charAt(_index);
            if (_escape === "u") {
                var _hex = value.substr(_index + 1, 4);
                if (!/^[0-9a-f]{4}$/i.test(_hex)) {
                    return _result;
                }
                _decoded += String.fromCharCode(parseInt(_hex, 16));
                _index += 4;
            } else if (_escape === "\"" || _escape === "\\" || _escape === "/") {
                _decoded += _escape;
            } else if (_escape === "b") {
                _decoded += "\b";
            } else if (_escape === "f") {
                _decoded += "\f";
            } else if (_escape === "n") {
                _decoded += "\n";
            } else if (_escape === "r") {
                _decoded += "\r";
            } else if (_escape === "t") {
                _decoded += "\t";
            } else {
                return _result;
            }
        } else {
            if (_character < " ") {
                return _result;
            }
            _decoded += _character;
        }
        _index++;
    }
    return _result;
}

function _skipJsonValue(value, start)
{
    if (value.charAt(start) === "\"") {
        var _string = _parseJsonString(value, start);
        return _string.valid ? _string.next : -1;
    }
    var _opening = value.charAt(start);
    if (_opening === "{" || _opening === "[") {
        var _stack = [_opening];
        var _index = start + 1;
        while (_index < value.length && _stack.length > 0) {
            var _character = value.charAt(_index);
            if (_character === "\"") {
                var _nestedString = _parseJsonString(value, _index);
                if (!_nestedString.valid) {
                    return -1;
                }
                _index = _nestedString.next;
            } else {
                if (_character === "{" || _character === "[") {
                    _stack.push(_character);
                } else if (_character === "}" || _character === "]") {
                    var _expected = _character === "}" ? "{" : "[";
                    if (_stack.pop() !== _expected) {
                        return -1;
                    }
                }
                _index++;
            }
        }
        return _stack.length === 0 ? _index : -1;
    }
    var _primitiveEnd = start;
    while (_primitiveEnd < value.length
           && value.charAt(_primitiveEnd) !== ","
           && value.charAt(_primitiveEnd) !== "}") {
        _primitiveEnd++;
    }
    return _primitiveEnd > start ? _primitiveEnd : -1;
}

function _skipWhitespace(value, start)
{
    var _index = start;
    while (_index < value.length && /\s/.test(value.charAt(_index))) {
        _index++;
    }
    return _index;
}

function _request(method, url, timeoutMilliseconds, body, contentType)
{
    var _result = { status: 0, text: "", error: "", timedOut: false };
    try {
        var _http = new ActiveXObject("MSXML2.ServerXMLHTTP.6.0");
        _http.open(method, url, false);
        var _connectionTimeout = Math.min(5000, timeoutMilliseconds);
        _http.setTimeouts(_connectionTimeout,
                          _connectionTimeout,
                          timeoutMilliseconds,
                          timeoutMilliseconds);
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
    if (!/^https?:\/\/[^?#]+$/i.test(value) || /\s/.test(value)) {
        return false;
    }

    var _authorityStart = value.indexOf("://") + 3;
    var _pathStart = value.indexOf("/", _authorityStart);
    var _authority = _pathStart >= 0
        ? value.substring(_authorityStart, _pathStart)
        : value.substring(_authorityStart);
    if (_authority.length === 0 || _authority.indexOf("@") >= 0) {
        return false;
    }

    try {
        // open validates the authority and port, including bracketed IPv6,
        // without sending a request or contacting the configured endpoint.
        var _validator = new ActiveXObject("MSXML2.ServerXMLHTTP.6.0");
        _validator.open("GET", value, false);
        _validator = null;
        return true;
    } catch (_error) {
        return false;
    }
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
