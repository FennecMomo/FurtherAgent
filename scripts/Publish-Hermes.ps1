$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
$releaseDir = Join-Path $repoRoot "artifacts/release/FurtherAgent-Hermes-$version-linux-x64"
New-Item -ItemType Directory -Force $releaseDir | Out-Null
dotnet publish (Join-Path $repoRoot 'src/FurtherAgent.Host') -c Release -r linux-x64 --self-contained true -p:UseAppHost=true -o $releaseDir
if ($LASTEXITCODE -ne 0) { throw 'Hermes host publish failed.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/Hermes.md'), (Join-Path $repoRoot 'docs/File-Transfer.md'), (Join-Path $repoRoot 'docs/Features-0.5.0-Validation.md'), (Join-Path $repoRoot 'docs/Stable-Pairing.md'), (Join-Path $repoRoot 'LICENSE'), (Join-Path $repoRoot 'deploy/furtheragent-hermes.service') -Destination $releaseDir -Force
$archive = "$releaseDir.tar.gz"
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/Multi-Controller-0.9.1.md') -Destination $releaseDir -Force
tar -czf $archive -C $releaseDir .
if ($LASTEXITCODE -ne 0) { throw 'Archive creation failed.' }
Get-FileHash -LiteralPath $archive -Algorithm SHA256
