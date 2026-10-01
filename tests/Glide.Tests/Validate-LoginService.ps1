#Requires -RunAsAdministrator
param([string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$package = if ($PackageDirectory) { (Resolve-Path -LiteralPath $PackageDirectory).Path } else { Join-Path $workspace 'artifacts\package\Glide' }
$result = Join-Path $workspace 'artifacts\login-service-validation.txt'
$serviceName = 'GlideSessionService'
$installed = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Glide\Glide.exe'
$data = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Glide\Service'
$probePath = Join-Path $data 'login-probe.txt'
$probeData = Join-Path $data 'LoginDpapi.probe'
$checks = [Collections.Generic.List[string]]::new()
$original = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
if (-not $original -or $original.StartName -ne 'LocalSystem' -or $original.PathName -ne ('"' + $installed + '" --service')) {
    throw 'This diagnostic requires an existing normal Glide service installation.'
}
$wasRunning = $original.State -eq 'Running'
$changed = $false
$exitCode = 1
function Pass([string]$Text) { $checks.Add('PASS ' + $Text); [IO.File]::WriteAllLines($result, $checks) }
function Require([bool]$Value, [string]$Text) { if (-not $Value) { throw $Text } }
function Read-Probe {
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $probePath) { $key,$value = $line -split '=',2; $values[$key] = $value }
    return $values
}
try {
    [IO.File]::WriteAllText($result, "Starting sign-in SYSTEM/session diagnostic; isolated loopback TLS, no input injection.`r`n")
    $changed = $true
    & (Join-Path $package 'Install-Service.ps1') -LoginDiagnosticMode -NoStart -SkipFirewall
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Diagnostic installation failed.' }
    foreach ($path in @($probePath,$probeData)) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
    $make = Start-Process -FilePath $installed -ArgumentList '--make-login-probe' -WindowStyle Hidden -Wait -PassThru
    Require ($make.ExitCode -eq 0) 'Could not create machine-DPAPI diagnostic fixture.'
    Start-Service $serviceName
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do { Start-Sleep -Milliseconds 200 } until ((Test-Path -LiteralPath $probePath) -or [DateTime]::UtcNow -gt $deadline)
    Require (Test-Path -LiteralPath $probePath) 'SYSTEM sign-in probe did not start; inspect the bounded service.log.'
    $probe = Read-Probe
    Require ($probe.System -eq 'True' -and $probe.Desktop -eq 'Winlogon') 'Probe is not SYSTEM on Winlogon.'
    Require ([int]$probe.Session -eq [Diagnostics.Process]::GetCurrentProcess().SessionId) 'Probe was not launched in the physical console session.'
    Require ($probe.MachineDPAPI -eq 'True') 'SYSTEM could not decrypt the enrolled-user machine-DPAPI fixture.'
    Require ($probe.LoopbackTLS -eq 'True' -and $probe.HookDesktop -eq 'Winlogon') 'SYSTEM TLS or sign-in hook initialization failed.'
    Require ($probe.Network -eq 'LoopbackOnly' -and $probe.Input -eq 'False') 'Probe unexpectedly enabled remote input/networking.'
    $childProcessId = [int]$probe.PID
    Pass 'fixed protected child starts as SYSTEM on physical-console Winlogon; no visible SYSTEM UI or input injection'
    Pass 'machine-DPAPI fixture encrypted by the enrolled user is decrypted by SYSTEM without a user password/profile token'
    Pass 'SYSTEM loads the decrypted certificate/private key, completes pinned TLS authentication on loopback, and initializes the input hook thread on Winlogon'
    Stop-Service $serviceName
    (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    $probe = Read-Probe
    Require ($probe.GracefulStop -eq 'True') 'Sign-in child did not exit gracefully.'
    Require (-not (Get-Process -Id $childProcessId -ErrorAction SilentlyContinue)) 'Sign-in child was orphaned.'
    Pass 'SCM stop signals graceful sign-in-child shutdown and leaves no orphan'
    $exitCode = 0
} catch {
    $checks.Add('FAIL ' + $_.Exception.Message)
    [IO.File]::WriteAllLines($result, $checks)
} finally {
    if ($changed) {
        try {
            $current = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
            $normalPath = '"' + $installed + '" --service'
            $testPath = '"' + $installed + '" --service-login-test'
            Require ($current -and $current.PathName -in @($normalPath,$testPath)) 'Refusing an unexpected service restoration target.'
            Stop-Service $serviceName -ErrorAction Stop
            (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
            $restore = Invoke-CimMethod -InputObject $current -MethodName Change -Arguments @{PathName=$normalPath;StartMode='Automatic'}
            Require ($restore.ReturnValue -eq 0) 'Normal service configuration restoration failed.'
            foreach ($path in @($probePath,$probeData)) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
            if ($wasRunning) { Start-Service $serviceName }
            $restored = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
            Require ($restored.PathName -eq $normalPath -and $restored.StartMode -eq 'Auto' -and $restored.StartName -eq 'LocalSystem') 'Normal automatic LocalSystem configuration did not persist.'
            Require (($restored.State -eq 'Running') -eq $wasRunning) 'Original running/stopped state was not restored.'
            Pass 'normal automatic service and its previous running/stopped state restored; existing pairing/settings/login opt-in preserved'
        } catch {
            $checks.Add('FAIL cleanup: ' + $_.Exception.Message)
            [IO.File]::WriteAllLines($result, $checks)
            $exitCode = 1
        }
    }
}
exit $exitCode
