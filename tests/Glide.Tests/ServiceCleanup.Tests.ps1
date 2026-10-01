# Offline fault injection: never calls SCM, launches Glide, or changes firewall rules.
$ErrorActionPreference = 'Stop'
$workspace = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$testRoot = Join-Path $workspace 'artifacts\service-cleanup'
New-Item -ItemType Directory -Path (Join-Path $testRoot 'artifacts'),(Join-Path $testRoot 'data'),(Join-Path $testRoot 'package') -Force | Out-Null
$validation = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Validate-Service.ps1')
$validation = $validation.Replace('#Requires -RunAsAdministrator', '# SCM is mocked by the offline regression runner.')
$validation = $validation.Replace('$workspace = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent', '$workspace = $PSScriptRoot')
$validation = $validation.Replace("`$data = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Glide\Service'", "`$data = Join-Path `$PSScriptRoot 'data'")
Set-Content -LiteralPath (Join-Path $testRoot 'Validate-Service.ps1') -Value $validation
@'
param([string]$SettingsPath, [switch]$DiagnosticMode, [switch]$NoStart, [switch]$SkipFirewall)
$global:mockPath = if ($DiagnosticMode) { $global:testPath } else { $global:normalPath }
if ($global:scenario -eq 'install-failure') { throw 'Injected installer failure after diagnostic configuration' }
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'package\Install-Service.ps1')
@'
param([string]$Scenario)
$ErrorActionPreference = 'Stop'
$global:scenario = $Scenario
$global:mockRoot = $PSScriptRoot
$exe = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Glide\Glide.exe'
$global:normalPath = '"' + $exe + '" --service'
$global:testPath = '"' + $exe + '" --service-test'
$global:mockPath = $normalPath
$global:mockPid = 100000
$global:stops = 0
$global:starts = 0
function Get-CimInstance { param($ClassName,$Filter) [pscustomobject]@{PathName=$global:mockPath;StartName='LocalSystem';StartMode='Auto'} }
function Get-Service {
    param($Name)
    $service = [pscustomobject]@{}
    $service | Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value { param($State,$Timeout) }
    $service
}
function Stop-Service {
    param($Name,$ErrorAction)
    $global:stops++
    $probe = Join-Path $global:mockRoot 'data\session-probe.txt'
    if (Test-Path -LiteralPath $probe) { Add-Content -LiteralPath $probe -Value 'GracefulStop=True' }
}
function Start-Service {
    param($Name)
    $global:starts++
    if ($global:scenario -eq 'start-failure') { throw 'Injected service start failure' }
    $global:mockPid++
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    Set-Content -LiteralPath (Join-Path $global:mockRoot 'data\session-probe.txt') -Value @("PID=$global:mockPid","Session=$session","User=$sid",'Elevated=1','System=False')
}
function Start-Process {
    param($FilePath,$ArgumentList,$WindowStyle,[switch]$Wait,[switch]$PassThru)
    Add-Content -LiteralPath (Join-Path $global:mockRoot 'data\session-probe.txt') -Value 'ShowRequest=True'
    [pscustomobject]@{ExitCode=0}
}
function Get-Process { param($Id,$ErrorAction) }
function Invoke-CimMethod {
    param($InputObject,$MethodName,$Arguments)
    $global:mockPath = $Arguments.PathName
    [pscustomobject]@{Path=$global:mockPath;Mode=$Arguments.StartMode;Stops=$global:stops;Starts=$global:starts} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $global:mockRoot 'restored.json')
    [pscustomobject]@{ReturnValue=0}
}
& (Join-Path $PSScriptRoot 'Validate-Service.ps1') -PackageDirectory (Join-Path $PSScriptRoot 'package')
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'Run-Mocked.ps1')
$shell = (Get-Process -Id $PID).Path
foreach ($scenario in @('install-failure','start-failure','success')) {
    $resultPath = Join-Path $testRoot 'restored.json'
    if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
    $arguments = @('-NoProfile','-File',('"' + (Join-Path $testRoot 'Run-Mocked.ps1') + '"'),'-Scenario',$scenario)
    $process = Start-Process -FilePath $shell -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $testRoot "$scenario.stdout.txt") -RedirectStandardError (Join-Path $testRoot "$scenario.stderr.txt")
    $expected = if ($scenario -eq 'success') { 0 } else { 1 }
    if ($process.ExitCode -ne $expected) { throw "Unexpected validation exit code for $scenario; see $testRoot" }
    if (-not (Test-Path -LiteralPath $resultPath)) { throw "Cleanup did not restore the service for $scenario" }
    $restored = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    if ($restored.Path -notlike '*" --service' -or $restored.Mode -ne 'Automatic' -or $restored.Stops -lt 1) { throw "Incorrect restored service configuration for $scenario" }
    if ($scenario -ne 'success' -and $restored.Starts -gt 1) { throw 'Cleanup restarted sharing after a failed validation' }
    Write-Output "PASS service validation restores normal configuration on $scenario (mocked SCM)"
}
