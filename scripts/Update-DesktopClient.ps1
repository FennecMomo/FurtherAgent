param([Parameter(Mandatory)][string]$ClientDirectory, [Parameter(Mandatory)][string]$Version, [switch]$AllowLegacyForce)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$source = (Resolve-Path -LiteralPath $ClientDirectory).Path
foreach ($name in @('FurtherAgent.Client.exe', 'FurtherAgent.Client.dll', 'FurtherAgent.Client.deps.json', 'FurtherAgent.Client.runtimeconfig.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $source $name) -PathType Leaf)) { throw "Incomplete client build: $name" }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.' }
$currentRoot = Join-Path $repoRoot 'artifacts/current'
$buildId = "$Version-$(Get-Date -Format 'yyyyMMdd-HHmmss')-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
$snapshot = Join-Path $currentRoot "builds/$buildId"
New-Item -ItemType Directory -Path $snapshot -Force | Out-Null
# Immutable snapshots keep any already-running client intact.
Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $snapshot -Recurse -Force
$target = Join-Path $snapshot 'FurtherAgent.Client.exe'
foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
    $relative = $file.FullName.Substring($source.Length).TrimStart('\', '/')
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $snapshot $relative)).Hash) {
        throw "Client snapshot verification failed: $relative"
    }
}
$desktop = [Environment]::GetFolderPath('Desktop')
$shell = New-Object -ComObject WScript.Shell
$shortcutPath = Join-Path $desktop 'FurtherAgent.lnk'
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$launcher = Join-Path $PSScriptRoot 'Start-LatestClient.ps1'
$shortcut.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$launcher`""
$shortcut.WorkingDirectory = $repoRoot
$shortcut.IconLocation = "$target,0"
$shortcut.Description = 'FurtherAgent - latest successfully published client'
$shortcut.Save()
# Publish the pointer last. A failed build/copy never replaces the previous target.
$manifestPath = Join-Path $currentRoot 'client.json'
$temp = Join-Path $currentRoot "$buildId.json.tmp"
@{ version = $Version; relativeExe = "builds/$buildId/FurtherAgent.Client.exe"; publishedAt = (Get-Date).ToString('o') } |
    ConvertTo-Json | Set-Content -LiteralPath $temp -Encoding utf8
if (Test-Path -LiteralPath $manifestPath) { [IO.File]::Replace($temp, $manifestPath, (Join-Path $currentRoot "$buildId.previous.json")) }
else { [IO.File]::Move($temp, $manifestPath) }
Write-Output "Desktop shortcut updated: $shortcutPath"
Write-Output "Current client: $target"
$running = @(Get-Process -Name FurtherAgent.Client -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith(([IO.Path]::GetFullPath((Join-Path $currentRoot 'builds')) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)
})
if ($running.Count -gt 0) {
    & (Join-Path $PSScriptRoot 'Client-Control.ps1') -Action Restart -ExecutablePath $target -AllowLegacyForce:$AllowLegacyForce
    if (!$?) { throw 'Build published, but running client switch failed; inspect client control status.' }
}
