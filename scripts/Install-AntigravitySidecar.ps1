#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$HostExecutable,
    [ValidateNotNullOrEmpty()][string]$ConfigRoot = (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.gemini/config'),
    [ValidateNotNullOrEmpty()][string]$DeviceName
)

$ErrorActionPreference = 'Stop'
$executable = Get-Item -LiteralPath $HostExecutable
if ($executable -isnot [IO.FileInfo] -or $executable.Name -notin @('FurtherAgent.Host.exe', 'FurtherAgent.Host')) {
    throw 'HostExecutable must point to a published FurtherAgent.Host executable.'
}

$configRootPath = [IO.Path]::GetFullPath($ConfigRoot)
$sidecarDirectory = Join-Path $configRootPath 'sidecars/furtheragent'
$manifestPath = Join-Path $sidecarDirectory 'sidecar.json'
$configPath = Join-Path $configRootPath 'config.json'
$description = 'FurtherAgent desktop integration (managed by Install-AntigravitySidecar.ps1)'

function Read-ExistingText([string]$path) {
    if (Test-Path -LiteralPath $path) { return [IO.File]::ReadAllText($path) }
    return $null
}

function Read-JsonObject([string]$path, $original) {
    if ($null -eq $original) { return @{} }
    $options = @{ InputObject = $original; AsHashtable = $true; NoEnumerate = $true }
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $options.DateKind = 'String' }
    $value = ConvertFrom-Json @options
    if ($value -isnot [Collections.IDictionary]) { throw "Expected a JSON object in $path" }
    return $value
}

function Write-JsonObject([string]$path, $original, [Collections.IDictionary]$value) {
    $json = ($value | ConvertTo-Json -Depth 100 -WarningAction Stop) + "`n"
    if ($original -ceq $json) { return }
    $temporary = "$path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
        if ((Read-ExistingText $path) -cne $original) { throw "Configuration changed while installing: $path. Retry the installer." }
        if ($null -eq $original) { [IO.File]::Move($temporary, $path) }
        else { [IO.File]::Replace($temporary, $path, "$path.fa-backup-$(Get-Date -Format yyyyMMddHHmmssfff)") }
    }
    finally { [IO.File]::Delete($temporary) }
}

$originalManifest = Read-ExistingText $manifestPath
$manifest = Read-JsonObject $manifestPath $originalManifest
if ($null -ne $originalManifest -and ($manifest.description -cne $description -or $manifest.env.FURTHERAGENT_BACKEND -cne 'antigravity')) {
    throw "The furtheragent sidecar already exists at $manifestPath. Its configuration was preserved."
}
if ($null -eq $originalManifest -and (Test-Path -LiteralPath $sidecarDirectory) -and
    (Get-ChildItem -LiteralPath $sidecarDirectory -Force | Select-Object -First 1)) {
    throw "The furtheragent sidecar directory already contains files: $sidecarDirectory"
}

$originalConfig = Read-ExistingText $configPath
$config = Read-JsonObject $configPath $originalConfig
if (!$config.Contains('sidecars')) { $config.sidecars = @{} }
if ($config.sidecars -isnot [Collections.IDictionary]) { throw "The sidecars setting in $configPath must be a JSON object." }
if (!$config.sidecars.Contains('furtheragent')) { $config.sidecars.furtheragent = @{} }
if ($config.sidecars.furtheragent -isnot [Collections.IDictionary]) { throw "The furtheragent setting in $configPath must be a JSON object." }

if (!$manifest.Contains('env')) { $manifest.env = @{} }
if ($PSBoundParameters.ContainsKey('DeviceName')) {
    if ([string]::IsNullOrWhiteSpace($DeviceName)) { throw 'DeviceName cannot be blank.' }
    $manifest.env.FURTHERAGENT_NAME = $DeviceName.Trim()
}
elseif ([string]::IsNullOrWhiteSpace($manifest.env.FURTHERAGENT_NAME)) {
    $manifest.env.FURTHERAGENT_NAME = '{0} {1} Antigravity' -f [Environment]::MachineName, [char]183
}
$manifest.env.FURTHERAGENT_BACKEND = 'antigravity'
$manifest.description = $description
$manifest.command = $executable.FullName
$manifest.args = @()
$manifest.display_name = 'FurtherAgent'
$manifest.restart_policy = 'on-failure'
$config.sidecars.furtheragent.enabled = $true

[IO.Directory]::CreateDirectory($sidecarDirectory) | Out-Null
Write-JsonObject $manifestPath $originalManifest $manifest
Write-JsonObject $configPath $originalConfig $config
Write-Output "Prepared the FurtherAgent sidecar: $manifestPath"
Write-Output 'Restart Antigravity to launch it. The sidecar log contains the pairing code.'
