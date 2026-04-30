var _status;
var _pass = "0";
try {
    var _endpoint = Session.Property("OLLAMAENDPOINT").replace(/\/+$/, "");
    var _http = new ActiveXObject("MSXML2.ServerXMLHTTP.6.0");
    _http.setTimeouts(5000, 5000, 5000, 5000);
    _http.open("GET", _endpoint, false);
    _http.send();
    if (_http.status === 200) {
        _status = "Connected - Ollama is running";
        _pass = "1";
    } else {
        _status = "Failed: HTTP " + _http.status;
    }
} catch(e) {
    _status = "Failed: cannot reach endpoint";
}
Session.Property("OLLAMASTATUS") = _status;
Session.Property("OLLAMATEST_PASS") = _pass;
