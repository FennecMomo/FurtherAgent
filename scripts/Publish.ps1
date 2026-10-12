param([switch]$AllowLegacyForce, [switch]$SkipDesktopUpdate)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.' }
$releaseDir = Join-Path $repoRoot "artifacts/release/$version"
New-Item -ItemType Directory -Force $releaseDir | Out-Null
dotnet publish (Join-Path $repoRoot 'src/FurtherAgent.Client') -c Release --self-contained false -o (Join-Path $releaseDir 'Client')
if ($LASTEXITCODE -ne 0) { throw 'Client publish failed.' }
dotnet publish (Join-Path $repoRoot 'src/FurtherAgent.Server') -c Release --self-contained false -o (Join-Path $releaseDir 'Server')
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }
foreach ($component in @('Host', 'Cli')) {
    dotnet publish (Join-Path $repoRoot "src/FurtherAgent.$component") -c Release --self-contained false -o (Join-Path $releaseDir $component)
    if ($LASTEXITCODE -ne 0) { throw "$component publish failed." }
}
New-Item -ItemType Directory -Force (Join-Path $releaseDir 'scripts'), (Join-Path $releaseDir 'plugins/furtheragent-dsh') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-DshBridge.ps1') -Destination (Join-Path $releaseDir 'scripts') -Force
Copy-Item -Path (Join-Path $repoRoot 'plugins/furtheragent-dsh/*') -Destination (Join-Path $releaseDir 'plugins/furtheragent-dsh') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md'), (Join-Path $repoRoot 'docs/Stable-Pairing.md'), (Join-Path $repoRoot 'LICENSE') -Destination $releaseDir -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-LocalServer.ps1') -Destination $releaseDir -Force
New-Item -ItemType Directory -Force (Join-Path $releaseDir 'docs') | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/Hermes.md'), (Join-Path $repoRoot 'docs/Session-UI-0.3.0.md'), (Join-Path $repoRoot 'docs/File-Transfer.md'), (Join-Path $repoRoot 'docs/Features-0.5.0-Validation.md'), (Join-Path $repoRoot 'docs/Hermes-0.8.1-Validation.md') -Destination (Join-Path $releaseDir 'docs') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/DSH.md'), (Join-Path $repoRoot 'docs/CLI.md') -Destination (Join-Path $releaseDir 'docs') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/Multi-Controller-0.9.1.md') -Destination (Join-Path $releaseDir 'docs') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/Multi-Connections-0.10.0.md') -Destination (Join-Path $releaseDir 'docs') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/Workspaces-0.11.0.md') -Destination (Join-Path $releaseDir 'docs') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/Codex-Theme-0.12.0.md') -Destination (Join-Path $releaseDir 'docs') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/Questions-0.12.2.md') -Destination (Join-Path $releaseDir 'docs') -Force
$zip = Join-Path $repoRoot "artifacts/release/FurtherAgent-$version.zip"
Compress-Archive -Path (Join-Path $releaseDir '*') -DestinationPath $zip -Force
Get-FileHash -LiteralPath $zip -Algorithm SHA256
if (-not $SkipDesktopUpdate) {
    & (Join-Path $PSScriptRoot 'Update-DesktopClient.ps1') -ClientDirectory (Join-Path $releaseDir 'Client') -Version $version -AllowLegacyForce:$AllowLegacyForce
}
