// EscapeAppSettingsProperties.js
// Pre-escape MSI properties before they get interpolated into the
// PatchAppSettings powershell.exe command line. WiX SetProperty substitutes
// [MSI_PROPERTY] literally inside the double-quoted argv tokens; if a
// property value contains a literal " (e.g., a MongoDB password that
// includes a quote) or ends with one or more \ characters that abut the
// wrapping " WiX adds, CommandLineToArgvW mis-parses the argument and the
// .ps1 either misses a parameter or receives a broken value. The resulting
// failure happens at the OS argv-parse step, before the .ps1's try/catch
// wrapper can record anything in the MSI log.
//
// The escape implemented here follows the full CommandLineToArgvW contract:
//   * A run of 2n backslashes followed by " parses as n literal backslashes
//     plus a closing quote -- so the run must be doubled before a " is
//     literal.
//   * A run of 2n+1 backslashes followed by " parses as n literal backslashes
//     plus a literal " (the trailing \ escapes the "). So we double the run
//     and then emit \" for the literal ".
//   * A trailing run of backslashes sits immediately before the close-quote
//     that the WiX SetProperty wrapper adds; same doubling rule applies.
//
// The C# mirror at SaddleRAG.Installer.Logic.ArgumentQuoteEscape implements
// the same algorithm and is unit-tested. If either side changes, update the
// other and re-run SaddleRAG.Tests/Installer/ArgumentQuoteEscapeTests.
//
// CA is registered Return="ignore" but the loop body is wrapped in a
// per-property try/catch that records any failure into the
// ESCAPE_FAILED MSI property so a future diagnostics CA (or operator
// inspecting the verbose log) can see which iteration broke. Without the
// per-property try/catch, a single Session.Property hiccup would leave the
// remaining _ESCAPED properties unset, PatchAppSettings would interpolate
// empty strings, and the install would "succeed" with broken downstream
// config.
//
// JScript style mirrors CheckOllamaKeepAlive.js / CheckGpuCapability.js.

var _props = [
    "MONGOCONNECTION",
    "MONGODATABASE",
    "OLLAMAENDPOINT",
    "ONNX_EXECUTION_PROVIDER"
];

function _escapeForCommandLine(value)
{
    var _result = "";
    var _backslashRun = 0;
    for (var _i = 0; _i < value.length; _i++) {
        var _ch = value.charAt(_i);
        if (_ch === "\\") {
            _backslashRun++;
        } else if (_ch === "\"") {
            // 2n backslashes before a literal " require doubling, then \".
            for (var _j = 0; _j < _backslashRun * 2; _j++) {
                _result += "\\";
            }
            _result += "\\\"";
            _backslashRun = 0;
        } else {
            // Any other char: emit pending backslashes verbatim, then the char.
            for (var _k = 0; _k < _backslashRun; _k++) {
                _result += "\\";
            }
            _result += _ch;
            _backslashRun = 0;
        }
    }
    // Trailing backslashes sit immediately before the close-quote WiX adds
    // around the substituted property value; same 2n-doubling rule.
    for (var _m = 0; _m < _backslashRun * 2; _m++) {
        _result += "\\";
    }
    return _result;
}

var _firstFailure = "";

for (var _idx = 0; _idx < _props.length; _idx++) {
    var _name = _props[_idx];
    try {
        // Defensive against properties that the package declares but a
        // prior CA hasn't yet populated; Session.Property returns "" for
        // declared-but-empty in WindowsInstaller's COM model, but the
        // `if (!_value)` keeps the contract crisp if that ever changes.
        var _value = Session.Property(_name);
        if (!_value) {
            _value = "";
        }
        var _escaped = _escapeForCommandLine(_value);
        Session.Property(_name + "_ESCAPED") = _escaped;
    } catch(_e) {
        if (_firstFailure.length === 0) {
            var _msg = (_e && _e.message) ? _e.message : "unknown";
            _firstFailure = _name + ": " + _msg;
        }
    }
}

Session.Property("ESCAPE_FAILED") = _firstFailure;
