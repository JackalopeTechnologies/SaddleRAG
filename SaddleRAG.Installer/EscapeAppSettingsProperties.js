// EscapeAppSettingsProperties.js
// Pre-escape MSI properties before they get interpolated into the
// PatchAppSettings powershell.exe command line. WiX SetProperty substitutes
// [MSI_PROPERTY] literally inside the double-quoted argv tokens; if a
// property value contains a literal " (e.g., a MongoDB password that
// includes a quote), CommandLineToArgvW terminates the argument early and
// the .ps1 either misses a parameter or receives a broken value. The
// resulting failure happens at the OS argv-parse step, before the .ps1's
// try/catch wrapper can record anything in the MSI log.
//
// This CA pre-substitutes " -> \" (per CommandLineToArgvW's rule that \"
// inside a quoted argument produces a literal " and continues quotation)
// and writes the escaped value to a sibling _ESCAPED property that the
// PatchAppSettings SetProperty consumes. Scheduled in InstallExecuteSequence
// right before PatchAppSettings; not needed in InstallUISequence because
// PatchAppSettings only runs in the Execute sequence.
//
// Realistic Mongo / Ollama inputs rarely contain quotes, so this is a
// defensive guard against an edge case rather than a hot path. JScript
// style mirrors CheckOllamaKeepAlive.js / CheckGpuCapability.js.

var _props = [
    "MONGOCONNECTION",
    "MONGODATABASE",
    "OLLAMAENDPOINT",
    "ONNX_EXECUTION_PROVIDER"
];

var _backslashQuote = "\\\""; // two chars: \ then "

for (var _i = 0; _i < _props.length; _i++) {
    var _name = _props[_i];
    var _value = Session.Property(_name);
    if (!_value) {
        _value = "";
    }
    var _escaped = _value.split("\"").join(_backslashQuote);
    Session.Property(_name + "_ESCAPED") = _escaped;
}
