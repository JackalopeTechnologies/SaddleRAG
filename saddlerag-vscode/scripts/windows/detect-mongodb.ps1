# detect-mongodb.ps1 — exit 0=running, 1=installed-not-running, 2=not-found
$ErrorActionPreference = "SilentlyContinue"

$svc = Get-Service -Name "MongoDB" -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq "Running") {
        Write-Output '{"status":"running","port":27017}'
        exit 0
    } else {
        $exe = (Get-Command mongod -ErrorAction SilentlyContinue)?.Source ?? ""
        Write-Output "{`"status`":`"stopped`",`"path`":`"$exe`"}"
        exit 1
    }
}

$mongod = Get-Command mongod -ErrorAction SilentlyContinue
if ($mongod) {
    Write-Output "{`"status`":`"stopped`",`"path`":`"$($mongod.Source)`"}"
    exit 1
}

Write-Output '{"status":"not-found"}'
exit 2
