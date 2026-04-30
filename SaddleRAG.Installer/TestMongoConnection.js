var _status;
var _pass = "0";
try {
    var _conn = Session.Property("MONGOCONNECTION");
    var _match = _conn.match(/\/\/([^:\/\?]+)(?::(\d+))?/);
    var _host = _match ? _match[1] : "localhost";
    var _port = (_match && _match[2]) ? parseInt(_match[2]) : 27017;
    var _shell = new ActiveXObject("WScript.Shell");
    var _fs = new ActiveXObject("Scripting.FileSystemObject");
    var _tmp = _fs.GetSpecialFolder(2);
    var _ps1 = _tmp + "\\saddlerag_mongo_test.ps1";
    var _out = _tmp + "\\saddlerag_mongo_result.txt";
    var _script = [
        "$h = '" + _host + "'",
        "$p = " + _port,
        "try {",
        "  $c = New-Object System.Net.Sockets.TcpClient",
        "  $c.Connect($h, $p)",
        "  $c.Close()",
        "  [System.IO.File]::WriteAllText('" + _out + "', 'OK', [System.Text.Encoding]::ASCII)",
        "} catch {",
        "  [System.IO.File]::WriteAllText('" + _out + "', $_.Exception.Message, [System.Text.Encoding]::ASCII)",
        "}"
    ].join("\r\n");
    var _f = _fs.CreateTextFile(_ps1, true);
    _f.Write(_script);
    _f.Close();
    _shell.Run("powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + _ps1 + "\"", 0, true);
    var _result = "";
    if (_fs.FileExists(_out)) {
        var _tf = _fs.OpenTextFile(_out, 1);
        _result = _tf.ReadAll();
        _tf.Close();
        _result = _result.replace(/[\r\n]+$/, "");
    }
    if (_result === "OK") {
        _status = "Connected to " + _host + ":" + _port;
        _pass = "1";
    } else {
        _status = "Failed: " + _result;
    }
    try { _fs.DeleteFile(_ps1); } catch(ex) {}
    try { _fs.DeleteFile(_out); } catch(ex) {}
} catch(e) {
    _status = "Error: " + e.message;
}
Session.Property("MONGOSTATUS") = _status;
Session.Property("MONGOTEST_PASS") = _pass;
