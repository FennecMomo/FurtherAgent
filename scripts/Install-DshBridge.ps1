param([ValidateSet('desktop','background')][string]$Mode='desktop',[string]$DshHome=(Join-Path $env:USERPROFILE '.dsh'))
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$root=Join-Path $env:LOCALAPPDATA 'FurtherAgent/dsh'
$manifest=Get-Content -LiteralPath (Join-Path $repoRoot 'plugins/furtheragent-dsh/package.json') -Raw | ConvertFrom-Json
$sources=@($manifest.files)+@('package.json') | ForEach-Object { Join-Path $repoRoot ('plugins/furtheragent-dsh/'+$_) }
$digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($sources | ForEach-Object {[IO.File]::ReadAllText($_)}) -join "`n"))).Substring(0,12).ToLowerInvariant()
$plugin=Join-Path $root ('plugin-'+$manifest.version+'-'+$digest)
if(!(Test-Path -LiteralPath $plugin)) {
    New-Item -ItemType Directory -Force -Path $plugin | Out-Null
    Copy-Item -LiteralPath $sources -Destination $plugin
}
$entry=([Uri]::new((Join-Path $plugin 'index.mjs'))).AbsoluteUri
$instances=(Join-Path $root 'instances').Replace('\','/')
$patch="`n# FurtherAgent same-Host bridge`n- insert:`n    - id: furtheragent-bridge`n      name: '$entry'`n      config:`n        mode: $Mode`n        directory: '$instances'`n"
if($Mode -eq 'desktop') {
    $target=Join-Path $DshHome 'profiles/desktop/cordis.patch.yml'
    if(!(Test-Path -LiteralPath $target)){throw 'Desktop profile not found. Start DSH once before installing.'}
    $content=[IO.File]::ReadAllText($target)
    if($content.Contains('id: furtheragent-bridge')) {
        $pattern="(?m)(    - id: furtheragent-bridge\r?\n      name: )[^\r\n]+"
        if([regex]::Matches($content,$pattern).Count -ne 1){throw 'Existing bridge block is ambiguous; no profile changes made.'}
        $updated=[regex]::Replace($content,$pattern,('$1'+"'$entry'"))
        if($updated -ne $content) {
            Copy-Item -LiteralPath $target -Destination ($target+'.fa-backup-'+(Get-Date -Format yyyyMMddHHmmssfff))
            [IO.File]::WriteAllText($target,$updated,[Text.UTF8Encoding]::new($false))
        }
        Write-Output 'Desktop bridge prepared at immutable versioned path. Restart DSH when idle to load it.';return
    }
    Copy-Item -LiteralPath $target -Destination ($target+'.fa-backup-'+(Get-Date -Format yyyyMMddHHmmss))
    [IO.File]::AppendAllText($target,$patch,[Text.UTF8Encoding]::new($false))
    Write-Output 'Desktop bridge configured. Existing settings preserved. No DSH process was restarted.'
} else {
    $target=Join-Path $root 'background.patch.yml'
    [IO.File]::WriteAllText($target,$patch,[Text.UTF8Encoding]::new($false))
    Write-Output 'Isolated background bridge prepared; no credentials or desktop sessions copied.'
}
