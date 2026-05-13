// CheckGpuCapability.js
// Detect whether this machine looks DirectX 12-capable so the installer can
// pre-select the right execution-provider radio in ExecutionModeDlg and so
// silent installs that don't pass ONNX_EXECUTION_PROVIDER pick the right
// default. Also reads the OS build number so the LaunchCondition can block
// install on Windows versions older than the DirectML floor (1903 / 18362).
//
// Sets four MSI properties:
//   WINDOWSBUILDNUMBER      : integer OS build (CurrentBuildNumber). Used by
//                             the LaunchCondition; 18362 is the floor for
//                             DirectML support.
//   GPU_DETECTED            : "1" if at least one non-Microsoft-Basic video
//                             controller is present, "0" otherwise. Drives the
//                             radio default in ExecutionModeDlg.
//   GPU_DETECTION_REASON    : human-readable string surfaced in the dialog.
//   ONNX_EXECUTION_PROVIDER : "DirectMl" when GPU_DETECTED=1, "Cpu" otherwise.
//                             The dialog's RadioButtonGroup reads this so the
//                             right radio renders selected; silent installs
//                             use it as the default written to appsettings.json
//                             via PatchAppSettings. The CA only writes when the
//                             property is currently empty, so an explicit
//                             command-line override (msiexec ... ONNX_EXECUTION_PROVIDER=Cpu)
//                             is preserved through both the UI and Execute
//                             sequence invocations of this CA. The Property
//                             declaration in Package.wxs intentionally has no
//                             default Value attribute so "empty == auto-detect"
//                             is the unambiguous initial state.
//
// JScript style mirrors CheckOllamaKeepAlive.js. Detection is intentionally
// permissive: a runtime EP-append failure in OnnxExecutionProviderConfigurator
// already falls back to CPU with a recorded warning, so a false positive here
// degrades gracefully rather than breaking the install.

var _buildNumber = 0;
var _gpuDetected = "0";
var _reason      = "";
var _gpuName     = "";
var _gpuDriver   = "";

// ---- OS build number from HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\CurrentBuildNumber.
// REG_SZ on every Windows release; WScript.Shell.RegRead returns the string,
// parseInt gives a numeric value the LaunchCondition can compare cleanly.
try {
    var _shell = new ActiveXObject("WScript.Shell");
    var _raw   = _shell.RegRead("HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\CurrentBuildNumber");
    _buildNumber = parseInt(_raw, 10);
    if (isNaN(_buildNumber)) {
        _buildNumber = 0;
    }
} catch(e) {
    _buildNumber = 0;
}

// ---- WMI Win32_VideoController scan. Permissive heuristic: any controller
// that isn't the Microsoft Basic Display Adapter or a Microsoft indirect /
// remote display driver counts as a real GPU. Runtime EP-append failure
// catches the rare misdetection.
function _isMicrosoftFallbackAdapter(name, pnp)
{
    var _n = (name || "").toLowerCase();
    var _p = (pnp  || "").toUpperCase();
    var _isBasic   = _n.indexOf("microsoft basic display") !== -1;
    var _isRemote  = _n.indexOf("microsoft remote display") !== -1;
    var _isIndDisp = _n.indexOf("microsoft indirect display") !== -1;
    var _isHyperV  = _n.indexOf("microsoft hyper-v video") !== -1;
    var _isPnpBasc = _p.indexOf("ROOT\\BASICDISPLAY") === 0;
    var _isPnpIdd  = _p.indexOf("ROOT\\INDIRECTDISPLAY") === 0;
    return _isBasic || _isRemote || _isIndDisp || _isHyperV || _isPnpBasc || _isPnpIdd;
}

try {
    var _wmi   = GetObject("winmgmts:\\\\.\\root\\cimv2");
    var _query = "SELECT Name, PNPDeviceID, DriverVersion, AdapterCompatibility FROM Win32_VideoController";
    var _items = _wmi.ExecQuery(_query);
    var _e     = new Enumerator(_items);
    while (!_e.atEnd()) {
        var _ctrl  = _e.item();
        var _name  = _ctrl.Name;
        var _pnp   = _ctrl.PNPDeviceID;
        var _drv   = _ctrl.DriverVersion;
        var _fake  = _isMicrosoftFallbackAdapter(_name, _pnp);
        if (!_fake) {
            _gpuDetected = "1";
            _gpuName     = _name   || "(unnamed adapter)";
            _gpuDriver   = _drv    || "unknown";
            break;
        }
        _e.moveNext();
    }
} catch(e) {
    _gpuDetected = "0";
    _reason = "GPU detection unavailable (WMI error: " + e.message + "); defaulting to CPU.";
}

// ---- Compose the reason string. LaunchCondition will block install on
// _buildNumber < 18362, but record it here too so the property is consistent
// and visible in verbose logs.
if (_reason === "") {
    if (_buildNumber > 0 && _buildNumber < 18362) {
        _reason = "Windows 10 version 1903 (build 18362) or later required for DirectML; this system reports build "
                  + _buildNumber + ". Install will be blocked.";
        _gpuDetected = "0";
    } else if (_gpuDetected === "1") {
        _reason = "Detected: " + _gpuName + " (driver " + _gpuDriver + "). DirectML pre-selected.";
    } else {
        _reason = "No DirectX 12-capable GPU detected; only Microsoft fallback adapter found. CPU pre-selected.";
    }
}

Session.Property("WINDOWSBUILDNUMBER")   = String(_buildNumber);
Session.Property("GPU_DETECTED")         = _gpuDetected;
Session.Property("GPU_DETECTION_REASON") = _reason;

// Only seed ONNX_EXECUTION_PROVIDER if it hasn't already been set by either
// the command line (msiexec /i ... ONNX_EXECUTION_PROVIDER=...) or a previous
// invocation of this CA (the CA runs in both the UI and Execute sequences
// for the silent-install case). RadioButtonGroup in ExecutionModeDlg writes
// to the same property when the user clicks a radio, so on the second
// invocation in the Execute sequence the user's choice is preserved.
var _currentProvider = Session.Property("ONNX_EXECUTION_PROVIDER");
if (!_currentProvider || _currentProvider.length === 0) {
    Session.Property("ONNX_EXECUTION_PROVIDER") = (_gpuDetected === "1") ? "DirectMl" : "Cpu";
}
