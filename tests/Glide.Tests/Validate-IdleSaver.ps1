param([string]$Executable, [string]$ExpectedSaver)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = if ($Executable) { (Resolve-Path -LiteralPath $Executable).Path } else { Join-Path $workspace 'artifacts\native\Glide.exe' }
$resultFolder = Join-Path $workspace 'artifacts\idle-saver-validation'
New-Item -ItemType Directory -Path $resultFolder -Force | Out-Null
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build Glide first.' }
# Windows caches the selected saver independently of its registry entry. Leave
# selection/password settings alone; select a different saver in Windows Screen
# Saver Settings before rerunning. Require its actual process as evidence.
$result = Join-Path $resultFolder 'current.txt'
if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result }
Write-Output 'Testing the currently selected saver via actual Windows idle timeout. Do not move/type locally until it returns. Runtime timeout restores automatically; selection/password settings stay unchanged.'
$process = Start-Process -FilePath $exe -ArgumentList ('--idle-saver-test "' + $result + '"') -WindowStyle Hidden -Wait -PassThru
if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
if ($process.ExitCode -ne 0) { throw "Actual idle saver validation failed; see $result" }
if ($ExpectedSaver -and (Get-Content -LiteralPath $result) -notcontains ('ActualSaverProcess=' + $ExpectedSaver)) {
    throw "Windows did not launch $ExpectedSaver; this is not compatibility evidence for that saver."
}
