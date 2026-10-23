param(
    [string]$AndroidSdkDirectory = $env:ANDROID_HOME,
    [string]$JavaSdkDirectory = $env:JAVA_HOME
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$projectFile = Join-Path $projectRoot 'src/FurtherAgent.Android/FurtherAgent.Android.csproj'
if (-not $AndroidSdkDirectory -or -not (Test-Path -LiteralPath $AndroidSdkDirectory)) { throw 'Specify -AndroidSdkDirectory or ANDROID_HOME.' }
if (-not $JavaSdkDirectory -or -not (Test-Path -LiteralPath (Join-Path $JavaSdkDirectory 'bin/java.exe'))) { throw 'Specify -JavaSdkDirectory or JAVA_HOME (JDK 21).' }
[xml]$project = Get-Content -LiteralPath $projectFile -Raw
$version = $project.Project.PropertyGroup.ApplicationDisplayVersion | Select-Object -First 1
$outputDirectory = Join-Path $projectRoot "artifacts/release/android/$version"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
# Release code with the local Android development signing identity: an internal preview APK, not a store release.
& dotnet publish $projectFile -c Release "-p:AndroidSdkDirectory=$AndroidSdkDirectory" "-p:JavaSdkDirectory=$JavaSdkDirectory" -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Android publish failed; no delivery APK was updated.' }
$signedApk = Get-ChildItem -LiteralPath $outputDirectory -Filter '*-Signed.apk' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $signedApk) { throw 'No signed APK was produced.' }
$delivery = Join-Path $outputDirectory "FurtherAgent-Android-$version.apk"
Copy-Item -LiteralPath $signedApk.FullName -Destination $delivery -Force
$hash = (Get-FileHash -LiteralPath $delivery -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($delivery))" | Set-Content -LiteralPath "$delivery.sha256" -Encoding utf8
Write-Output "APK: $delivery"
Write-Output "SHA256: $hash"
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/Android-Completion-Notifications.md'), (Join-Path $projectRoot 'docs/Pending-Tests.md') -Destination $outputDirectory -Force
