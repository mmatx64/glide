# Fault injection for the SYSTEM diagnostic; all service/process commands are mocked.
$ErrorActionPreference = 'Stop'
$workspace = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$testRoot = Join-Path $workspace 'artifacts\login-service-cleanup'
New-Item -ItemType Directory -Path (Join-Path $testRoot 'artifacts'),(Join-Path $testRoot 'data'),(Join-Path $testRoot 'package') -Force | Out-Null
$validation = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Validate-LoginService.ps1')
$validation = $validation.Replace('#Requires -RunAsAdministrator', '# Offline mocked SCM.')
$validation = $validation.Replace('$workspace = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent', '$workspace = $PSScriptRoot')
$validation = $validation.Replace("`$data = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Glide\Service'", "`$data = Join-Path `$PSScriptRoot 'data'")
Set-Content -LiteralPath (Join-Path $testRoot 'Validate-LoginService.ps1') -Value $validation
@'
param([switch]$LoginDiagnosticMode, [switch]$NoStart, [switch]$SkipFirewall)
$global:mockPath = $global:testPath
$global:mockState = 'Stopped'
if ($global:scenario -eq 'install-failure') { throw 'Injected install failure' }
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'package\Install-Service.ps1')
@'
param([string]$Scenario)
$ErrorActionPreference = 'Stop'
$global:scenario = $Scenario
$global:mockRoot = $PSScriptRoot
$exe = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Glide\Glide.exe'
$global:normalPath = '"' + $exe + '" --service'
$global:testPath = '"' + $exe + '" --service-login-test'
$global:mockPath = $normalPath
$global:mockState = if ($Scenario -eq 'success-stopped') { 'Stopped' } else { 'Running' }
$global:starts = 0
function Get-CimInstance {
    param($ClassName,$Filter)
    [pscustomobject]@{PathName=$global:mockPath;StartName='LocalSystem';StartMode='Auto';State=$global:mockState}
}
function Get-Service {
    param($Name)
    $service = [pscustomobject]@{}
    $service | Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value { param($State,$Timeout) }
    $service
}
function Stop-Service {
    param($Name,$ErrorAction)
    $global:mockState = 'Stopped'
    $probe = Join-Path $global:mockRoot 'data\login-probe.txt'
    if (Test-Path -LiteralPath $probe) { Add-Content -LiteralPath $probe -Value 'GracefulStop=True' }
}
function Start-Service {
    param($Name)
    $global:starts++
    if ($global:mockPath -eq $global:testPath) {
        if ($global:scenario -eq 'start-failure') { throw 'Injected start failure' }
        $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
        Set-Content -LiteralPath (Join-Path $global:mockRoot 'data\login-probe.txt') -Value @(
            'PID=100000',"Session=$session",'System=True','Desktop=Winlogon','MachineDPAPI=True',
            'HookDesktop=Winlogon','LoopbackTLS=True','Network=LoopbackOnly','Input=False')
    }
    $global:mockState = 'Running'
}
function Start-Process {
    param($FilePath,$ArgumentList,$WindowStyle,[switch]$Wait,[switch]$PassThru)
    Set-Content -LiteralPath (Join-Path $global:mockRoot 'data\LoginDpapi.probe') -Value 'fixture'
    [pscustomobject]@{ExitCode=0}
}
function Get-Process { param($Id,$ErrorAction) }
function Invoke-CimMethod {
    param($InputObject,$MethodName,$Arguments)
    $global:mockPath = $Arguments.PathName
    [pscustomobject]@{ReturnValue=0}
}
$global:LASTEXITCODE = 0
& (Join-Path $PSScriptRoot 'Validate-LoginService.ps1') -PackageDirectory (Join-Path $PSScriptRoot 'package')
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'Run-Mocked.ps1')
$shell = (Get-Process -Id $PID).Path
foreach ($scenario in @('install-failure','start-failure','success','success-stopped')) {
    Set-Content -LiteralPath (Join-Path $testRoot 'data\LoginControl.enabled') -Value 'preserve-opt-in'
    Set-Content -LiteralPath (Join-Path $testRoot 'data\LoginReceiver.dat') -Value 'preserve-pairing'
    $arguments = @('-NoProfile','-File',('"' + (Join-Path $testRoot 'Run-Mocked.ps1') + '"'),'-Scenario',$scenario)
    $process = Start-Process -FilePath $shell -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $testRoot "$scenario.stdout.txt") -RedirectStandardError (Join-Path $testRoot "$scenario.stderr.txt")
    $expected = if ($scenario -like 'success*') { 0 } else { 1 }
    if ($process.ExitCode -ne $expected) { throw "Unexpected SYSTEM diagnostic exit code for $scenario; see $testRoot" }
    $result = Get-Content -Raw -LiteralPath (Join-Path $testRoot 'artifacts\login-service-validation.txt')
    if ($result -notlike '*PASS normal automatic service and its previous running/stopped state restored*' -or $result -like '*FAIL cleanup:*') { throw "SYSTEM diagnostic restoration failed on $scenario" }
    if ((Get-Content -Raw -LiteralPath (Join-Path $testRoot 'data\LoginControl.enabled')).Trim() -ne 'preserve-opt-in' -or
        (Get-Content -Raw -LiteralPath (Join-Path $testRoot 'data\LoginReceiver.dat')).Trim() -ne 'preserve-pairing') { throw 'Diagnostic changed existing opt-in/pairing.' }
    if ((Test-Path -LiteralPath (Join-Path $testRoot 'data\LoginDpapi.probe')) -or (Test-Path -LiteralPath (Join-Path $testRoot 'data\login-probe.txt'))) { throw 'Diagnostic fixture was not cleaned.' }
    Write-Output "PASS sign-in diagnostic restores state and preserves opt-in/pairing on $scenario (mocked SCM)"
}
