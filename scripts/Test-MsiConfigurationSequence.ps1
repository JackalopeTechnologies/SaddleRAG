#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$resolved = (Resolve-Path -LiteralPath $Path).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($resolved, 0)
$view = $database.OpenView('SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`')
$view.Execute()
$sequence = @{}
while ($record = $view.Fetch()) { $sequence[$record.StringData(1)] = $record.IntegerData(2) }
$view.Close()

# Inspect the compiled table. Old-product removal must precede script actions
# (ICE63); rollback restoration must precede new-product file/configuration work.
$requiredOrder = @(
    'SnapshotConfiguration', 'InstallInitialize', 'RemoveExistingProducts',
    'RollbackConfiguration', 'InstallFiles', 'RestoreConfiguration',
    'CompleteConfiguration', 'EscapeAppSettingsProperties', 'PatchAppSettings',
    'StartAndMonitorService', 'InstallFinalize'
)
$previous = -1
foreach ($action in $requiredOrder) {
    if (!$sequence.ContainsKey($action) -or $sequence[$action] -le $previous) {
        throw "Configuration preservation has an invalid compiled action order at $action."
    }
    $previous = $sequence[$action]
}
Write-Output 'PASS compiled MSI configuration preservation and rollback ordering'
