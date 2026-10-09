$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.' }
$releaseDir = Join-Path $repoRoot "artifacts/release/FurtherAgent-Relay-$version-linux-x64"
New-Item -ItemType Directory -Force $releaseDir | Out-Null
dotnet publish (Join-Path $repoRoot 'src/FurtherAgent.Server') -c Release -r linux-x64 --self-contained true -p:UseAppHost=true -o $releaseDir
if ($LASTEXITCODE -ne 0) { throw 'Relay publish failed.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/Relay.md'), (Join-Path $repoRoot 'LICENSE'), (Join-Path $repoRoot 'deploy/furtheragent-relay.service') -Destination $releaseDir -Force
$archive = "$releaseDir.tar.gz"
tar -czf $archive -C $releaseDir .
if ($LASTEXITCODE -ne 0) { throw 'Archive creation failed.' }
Get-FileHash -LiteralPath $archive -Algorithm SHA256
