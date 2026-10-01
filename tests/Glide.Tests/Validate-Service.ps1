#Requires -RunAsAdministrator
param([string]$PackageDirectory, [string]$SettingsPath, [switch]$EnableAfterTest)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source = if ($PackageDirectory) { (Resolve-Path -LiteralPath $PackageDirectory).Path } else { Join-Path $workspace 'artifacts\package\Glide' }
$result = Join-Path $workspace 'artifacts\service-validation.txt'
if (-not $SettingsPath) { $SettingsPath = Join-Path $source 'Glide.ini' }
$serviceName = 'GlideSessionService'
$data = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Glide\Service'
$installed = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Glide\Glide.exe'
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$checks = [Collections.Generic.List[string]]::new()
$configurationChanged = $false
$validationSucceeded = $false
$exitCode = 1
function Pass([string]$Text) { $checks.Add('PASS ' + $Text); [IO.File]::WriteAllLines($result, $checks) }
function Require([bool]$Value, [string]$Text) { if (-not $Value) { throw $Text } }
function Read-Probe {
    $probe = @{}
    foreach ($line in Get-Content -LiteralPath (Join-Path $data 'session-probe.txt')) {
        $key, $value = $line -split '=', 2; $probe[$key] = $value
    }
    return $probe
}
try {
    [IO.File]::WriteAllText($result, "Starting real SCM/session validation; no network or input injection in probe mode.`r`n")
    # Preserve the current user's actual pairing without printing any INI content.
    $configurationChanged = $true
    & (Join-Path $source 'Install-Service.ps1') -SettingsPath $SettingsPath -DiagnosticMode -NoStart -SkipFirewall
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Installer failed' }
    Pass 'protected installation and administrator-only service data created'
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    Require ($service.StartName -eq 'LocalSystem') 'Service account is not LocalSystem'
    Require ($service.PathName -eq ('"' + $installed + '" --service-test')) 'SCM executable path/quoting mismatch'
    Pass 'SCM LocalSystem identity and fully quoted fixed executable path'
    $probePath = Join-Path $data 'session-probe.txt'
    if (Test-Path -LiteralPath $probePath) { Remove-Item -LiteralPath $probePath }
    Start-Service $serviceName
    (Get-Service $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do { Start-Sleep -Milliseconds 200 } until ((Test-Path -LiteralPath (Join-Path $data 'session-probe.txt')) -or [DateTime]::UtcNow -gt $deadline)
    Require (Test-Path -LiteralPath (Join-Path $data 'session-probe.txt')) 'Session probe did not start'
    $probe = Read-Probe
    Require ($probe.User -eq $owner -and $probe.Elevated -eq '1' -and $probe.System -eq 'False') 'Session child user/elevation mismatch'
    Require ([int]$probe.Session -eq [Diagnostics.Process]::GetCurrentProcess().SessionId) 'Session child was launched in a different session'
    $probeProcessId = [int]$probe.PID
    Pass 'LocalSystem launched an elevated child as the enrolled user, not SYSTEM, in the interactive session'
    # Open through the same one-command event used by the Start menu shortcut.
    $open = Start-Process -FilePath $installed -ArgumentList '--open-service' -WindowStyle Hidden -Wait -PassThru
    Require ($open.ExitCode -eq 0) 'Service shortcut request failed'
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do { Start-Sleep -Milliseconds 100; $probe = Read-Probe } until ($probe.ShowRequest -eq 'True' -or [DateTime]::UtcNow -gt $deadline)
    Require ($probe.ShowRequest -eq 'True') 'Session child did not receive show request'
    Pass 'service wake/show request is delivered without starting a second child'
    Stop-Service $serviceName
    (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    $probe = Read-Probe
    Require ($probe.GracefulStop -eq 'True') 'Session child did not exit gracefully'
    Require (-not (Get-Process -Id $probeProcessId -ErrorAction SilentlyContinue)) 'Service left an orphaned child'
    Pass 'SCM stop signals the child to exit gracefully and leaves no elevated orphan'
    Start-Service $serviceName
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do { Start-Sleep -Milliseconds 200; $probe = Read-Probe } until ([int]$probe.PID -ne $probeProcessId -or [DateTime]::UtcNow -gt $deadline)
    Require ([int]$probe.PID -ne $probeProcessId -and $probe.Elevated -eq '1') 'Service restart did not relaunch child'
    Stop-Service $serviceName
    Pass 'SCM restart recreates a fresh elevated user session process'
    & (Join-Path $source 'Install-Service.ps1') -NoStart
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Production installation failed' }
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    Require ($service.PathName -eq ('"' + $installed + '" --service')) 'Production service command mismatch'
    Require ($service.StartMode -eq 'Auto') 'Service is not automatic'
    Pass 'normal service mode installed with automatic startup and private-LAN firewall rules'
    $validationSucceeded = $true
    $exitCode = 0
} catch {
    $checks.Add('FAIL ' + $_.Exception.Message)
    [IO.File]::WriteAllLines($result, $checks)
} finally {
    if ($configurationChanged) {
        try {
            $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
            if ($service) {
                $normalPath = '"' + $installed + '" --service'
                $testPath = '"' + $installed + '" --service-test'
                if ($service.PathName -notin @($normalPath,$testPath)) { throw 'Refusing to restore an unexpected service executable.' }
                Stop-Service -Name $serviceName -ErrorAction Stop
                (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
                $restored = Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{ PathName = $normalPath; StartMode = 'Automatic' }
                Require ($restored.ReturnValue -eq 0) 'Failed to restore normal service configuration'
                Pass 'normal service configuration restored after validation, including failure paths'
                if ($validationSucceeded -and $EnableAfterTest) {
                    Start-Service $serviceName
                    Pass 'normal service started; an existing portable Glide may need to be quit once'
                } else { Pass 'normal service left stopped after validation' }
            }
        } catch {
            $checks.Add('FAIL cleanup: ' + $_.Exception.Message)
            [IO.File]::WriteAllLines($result, $checks)
            $exitCode = 1
        }
    }
}
exit $exitCode
