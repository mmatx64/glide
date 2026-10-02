# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Glide contributors

param([switch]$SkipTests, [switch]$SkipLocalCopy)
$releaseVersion = '0.7.4'
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (-not $SkipTests -and (Test-Path -LiteralPath 'tests/Glide.Tests/Glide.Tests.csproj')) {
        dotnet run --project tests/Glide.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
        & (Join-Path $PSScriptRoot 'tests/Glide.Tests/ServiceCleanup.Tests.ps1')
        & (Join-Path $PSScriptRoot 'tests/Glide.Tests/LoginServiceCleanup.Tests.ps1')
    }
    dotnet publish src/Glide.App/Glide.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/native --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
    # Include the complete GPL in the bundled README, keeping the fixed archive
    # layout compatible with existing updaters. The GitHub README stays concise.
    $bundledReadme = (Get-Content -LiteralPath README.md -Raw) + "`r`n`r`n## Full license`r`n`r`n~~~text`r`n" +
        (Get-Content -LiteralPath LICENSE -Raw) + "`r`n~~~`r`n"
    $releaseFolder = Join-Path $PSScriptRoot 'dist/Glide'
    if (-not $SkipLocalCopy) {
        New-Item -ItemType Directory -Force $releaseFolder | Out-Null
        Copy-Item -LiteralPath artifacts/native/Glide.exe -Destination $releaseFolder -Force
        # Never overwrite a user's configured INI during a rebuild.
        if (-not (Test-Path -LiteralPath (Join-Path $releaseFolder 'Glide.ini'))) {
            Copy-Item -LiteralPath Glide.ini -Destination $releaseFolder
        }
        Copy-Item -LiteralPath README.md,Allow-PrivateNetwork.ps1,Install-Service.ps1 -Destination $releaseFolder -Force
        Set-Content -LiteralPath (Join-Path $releaseFolder 'README.md') -Value $bundledReadme -Encoding utf8
    }
    if (-not $SkipTests) {
        $nativeTest = Start-Process -FilePath (Join-Path $PSScriptRoot 'artifacts/native/Glide.exe') `
            -ArgumentList '--self-test' -WindowStyle Hidden -PassThru -Wait
        if ($nativeTest.ExitCode -ne 0) { throw 'Native smoke test failed. See artifacts/native/self-test/results.txt.' }
    }
    # Package a fresh template, never a working folder's potentially private settings.
    $packageFolder = Join-Path $PSScriptRoot 'artifacts/package/Glide'
    New-Item -ItemType Directory -Force $packageFolder | Out-Null
    Copy-Item -LiteralPath artifacts/native/Glide.exe,Glide.ini,README.md,Allow-PrivateNetwork.ps1,Install-Service.ps1 -Destination $packageFolder -Force
    Set-Content -LiteralPath (Join-Path $packageFolder 'README.md') -Value $bundledReadme -Encoding utf8
    $packageFiles = 'Glide.exe','Glide.ini','README.md','Allow-PrivateNetwork.ps1','Install-Service.ps1' | ForEach-Object { Join-Path $packageFolder $_ }
    $zipPath = "dist/Glide-$releaseVersion-win-x64.zip"
    New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot 'dist') | Out-Null
    Compress-Archive -LiteralPath $packageFiles -DestinationPath $zipPath -Force
    $zipChecksum = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant() + "  Glide-$releaseVersion-win-x64.zip"
    $exeChecksum = (Get-FileHash -LiteralPath artifacts/native/Glide.exe -Algorithm SHA256).Hash.ToLowerInvariant() + '  Glide.exe'
    Set-Content -LiteralPath "dist/SHA256SUMS-$releaseVersion.txt" -Value @($zipChecksum, $exeChecksum) -Encoding ascii
    Get-Item artifacts/native/Glide.exe,$zipPath | Select-Object FullName,Length
} finally { Pop-Location }
