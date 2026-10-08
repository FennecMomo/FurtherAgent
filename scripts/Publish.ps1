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
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md'), (Join-Path $repoRoot 'LICENSE') -Destination $releaseDir -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-LocalServer.ps1') -Destination $releaseDir -Force
$zip = Join-Path $repoRoot "artifacts/release/FurtherAgent-$version.zip"
Compress-Archive -Path (Join-Path $releaseDir '*') -DestinationPath $zip -Force
Get-FileHash -LiteralPath $zip -Algorithm SHA256
