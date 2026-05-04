// CheckOllamaKeepAlive.js
// Read the system OLLAMA_KEEP_ALIVE environment variable so the Ollama dialog
// can decide whether to show the "we will set OLLAMA_KEEP_ALIVE=-1" notice.
//
// Sets three MSI properties:
//   OLLAMA_KEEPALIVE_EXISTING : the current value, or empty if not set
//   OLLAMA_KEEPALIVE_WILL_SET : "1" if SaddleRAG will set it during install,
//                               "0" if the user already has a value we should respect
//   OLLAMA_KEEPALIVE_NOTICE   : the notice text to render in the Ollama dialog,
//                               or empty when the user already has a value
//                               (using property substitution in Text="" instead
//                                of show/hide conditions, which WiX 5 does not
//                                accept as child elements of <Control>)
//
// JScript style mirrors TestOllamaConnection.js / TestMongoConnection.js.

var _existing = "";
var _willSet  = "0";
var _notice   = "";

try {
    var _shell = new ActiveXObject("WScript.Shell");
    var _env   = _shell.Environment("SYSTEM");
    _existing  = _env("OLLAMA_KEEP_ALIVE");

    if (!_existing || _existing.length === 0) {
        _willSet = "1";
    }
} catch(e) {
    // If we can't read it for any reason, assume we will set it. Worst case
    // the install-time PS script will discover an existing value and back off.
    _willSet = "1";
}

if (_willSet === "1") {
    _notice = "SaddleRAG will set OLLAMA_KEEP_ALIVE=-1 system-wide so Ollama keeps its embedding model resident. Reboot recommended; setting is removed on uninstall.";
}

Session.Property("OLLAMA_KEEPALIVE_EXISTING") = _existing;
Session.Property("OLLAMA_KEEPALIVE_WILL_SET") = _willSet;
Session.Property("OLLAMA_KEEPALIVE_NOTICE")   = _notice;
