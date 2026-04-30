var _status;
try {
    var _endpoint = Session.Property("OLLAMAENDPOINT").replace(/\/+$/, "");
    var _http = new ActiveXObject("MSXML2.ServerXMLHTTP.6.0");
    _http.setTimeouts(5000, 5000, 5000, 5000);
    _http.open("GET", _endpoint, false);
    _http.send();
    _status = (_http.status === 200) ? "Connected - Ollama is running" : ("Failed: HTTP " + _http.status);
} catch(e) {
    _status = "Failed: cannot reach endpoint";
}
Session.Property("OLLAMASTATUS") = _status;
