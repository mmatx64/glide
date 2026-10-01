param(
    [ValidateSet('Install','Uninstall','Status')][string]$Action = 'Install',
    [string]$SettingsPath,
    [switch]$NoStart,
    [switch]$SkipFirewall,
    [switch]$DiagnosticMode,
    [switch]$LoginDiagnosticMode,
    [switch]$EnableLoginControl,
    [switch]$DisableLoginControl
)
$ErrorActionPreference = 'Stop'
$serviceName = 'GlideSessionService'
$installFolder = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Glide'
$dataRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Glide'
$dataFolder = Join-Path $dataRoot 'Service'
$installedExe = Join-Path $installFolder 'Glide.exe'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Glide (service).lnk'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$ownerSid = $identity.User.Value
$admin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($EnableLoginControl -and $DisableLoginControl) { throw 'Choose EnableLoginControl or DisableLoginControl.' }
if ($DiagnosticMode -and $LoginDiagnosticMode) { throw 'Choose one diagnostic mode.' }

if ($Action -eq 'Status') {
    Get-Service -Name $serviceName -ErrorAction SilentlyContinue | Select-Object Name,Status,StartType
    Write-Output "Installed executable: $installedExe"
    Write-Output "Service settings and log: $dataFolder"
    exit 0
}
if (-not $admin) {
    # The user approves one ordinary UAC prompt. No password is collected here.
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Action ' + $Action
    if ($SettingsPath) {
        $SettingsPath = (Resolve-Path -LiteralPath $SettingsPath).Path
        $arguments += ' -SettingsPath "' + $SettingsPath + '"'
    }
    if ($NoStart) { $arguments += ' -NoStart' }
    if ($SkipFirewall) { $arguments += ' -SkipFirewall' }
    if ($DiagnosticMode) { $arguments += ' -DiagnosticMode' }
    if ($LoginDiagnosticMode) { $arguments += ' -LoginDiagnosticMode' }
    if ($EnableLoginControl) { $arguments += ' -EnableLoginControl' }
    if ($DisableLoginControl) { $arguments += ' -DisableLoginControl' }
    $setup = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Verb RunAs -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    exit $setup.ExitCode
}

function Assert-NoReparse([string]$Path) {
    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        if ((Test-Path -LiteralPath $candidate) -and ((Get-Item -Force -LiteralPath $candidate).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a reparse point in service path: $candidate"
        }
        $candidate = [IO.Path]::GetDirectoryName($candidate)
    }
}
function Protect-Directory([string]$Path, [bool]$AllowRead) {
    Assert-NoReparse $Path
    if (Test-Path -LiteralPath $Path) {
        $existingOwner = (Get-Acl -LiteralPath $Path).GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($existingOwner -notin @('S-1-5-18','S-1-5-32-544')) { throw "Service directory has an untrusted owner: $Path" }
    } else { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]'S-1-5-32-544')
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule([Security.Principal.SecurityIdentifier]$sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    if ($AllowRead) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule([Security.Principal.SecurityIdentifier]'S-1-5-32-545', 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Check-Native([string]$Operation) { if ($LASTEXITCODE -ne 0) { throw "$Operation failed with code $LASTEXITCODE" } }

try {
    Assert-NoReparse $installFolder; Assert-NoReparse $dataFolder
    $existing = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    $normalBinaryPath = '"' + $installedExe + '" --service'
    $testBinaryPath = '"' + $installedExe + '" --service-test'
    $loginTestBinaryPath = '"' + $installedExe + '" --service-login-test'
    $binaryPath = if ($DiagnosticMode) { $testBinaryPath } elseif ($LoginDiagnosticMode) { $loginTestBinaryPath } else { $normalBinaryPath }
    if ($existing -and $existing.PathName -notin @($normalBinaryPath,$testBinaryPath,$loginTestBinaryPath)) { throw 'Existing service points to an unexpected executable. Refusing to replace it.' }
    if ($existing -and $existing.StartName -ne 'LocalSystem') { throw 'The existing Glide service must run as LocalSystem.' }
    if ($Action -eq 'Uninstall') {
        if ($existing) {
            Stop-Service -Name $serviceName -Force
            (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
            & sc.exe delete $serviceName | Out-Null; Check-Native 'Remove service'
        }
        if (Test-Path -LiteralPath $shortcutPath) { Assert-NoReparse $shortcutPath; Remove-Item -LiteralPath $shortcutPath }
        Get-NetFirewallRule -Name 'Glide-Service-*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        Write-Output 'Glide service removed. Installed files and settings were retained; portable Glide is unchanged.'
        exit 0
    }
    $sourceExe = Join-Path $PSScriptRoot 'Glide.exe'
    if (-not (Test-Path -LiteralPath $sourceExe)) { throw 'Run the installer beside the packaged Glide.exe.' }
    if ([IO.Path]::GetFullPath($sourceExe) -eq $installedExe) { throw 'Install/update from an extracted release folder, not Program Files.' }
    Assert-NoReparse $sourceExe
    $ownerFile = Join-Path $installFolder 'Owner.sid'
    if (Test-Path -LiteralPath $ownerFile) {
        Assert-NoReparse $ownerFile
        if ((Get-Content -Raw -LiteralPath $ownerFile).Trim() -ne $ownerSid) { throw 'This installation belongs to another Windows account. Run setup elevated as the enrolled account.' }
    }
    if ($existing) {
        Stop-Service -Name $serviceName -Force
        (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    }
    Protect-Directory $installFolder $true
    Protect-Directory $dataRoot $false
    Protect-Directory $dataFolder $false
    foreach ($name in @('Glide.exe','Owner.sid')) { Assert-NoReparse (Join-Path $installFolder $name) }
    $dataNames = @('Glide.ini','Glide.ini.tmp','service.log','service.log.previous','LoginControl.enabled','LoginReceiver.dat','LoginReceiver.dat.tmp','LoginDpapi.probe','login-probe.txt')
    foreach ($name in $dataNames) { Assert-NoReparse (Join-Path $dataFolder $name) }
    Copy-Item -LiteralPath $sourceExe -Destination $installedExe -Force
    Set-Content -LiteralPath $ownerFile -Value $ownerSid -Encoding ascii
    $targetSettings = Join-Path $dataFolder 'Glide.ini'
    if (-not (Test-Path -LiteralPath $targetSettings)) {
        if (-not $SettingsPath) { $SettingsPath = Join-Path $PSScriptRoot 'Glide.ini' }
        Copy-Item -LiteralPath (Resolve-Path -LiteralPath $SettingsPath).Path -Destination $targetSettings
    }
    # Reset explicit child ACLs as well, including files retained across upgrades.
    foreach ($file in @($installedExe,$ownerFile)) { & icacls.exe $file /reset | Out-Null; Check-Native 'Protect installed files' }
    foreach ($name in $dataNames) {
        $file = Join-Path $dataFolder $name
        if (Test-Path -LiteralPath $file) { & icacls.exe $file /reset | Out-Null; Check-Native 'Protect service data' }
    }
    $loginMarker = Join-Path $dataFolder 'LoginControl.enabled'
    if ($DisableLoginControl) {
        foreach ($name in @('LoginControl.enabled','LoginReceiver.dat','LoginReceiver.dat.tmp')) {
            $file = Join-Path $dataFolder $name
            if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
        }
    } elseif ($EnableLoginControl) { Set-Content -LiteralPath $loginMarker -Value $ownerSid -Encoding ascii }
    if (Test-Path -LiteralPath $loginMarker) {
        $enroll = Start-Process -FilePath $installedExe -ArgumentList '--enroll-login-control' -WindowStyle Hidden -Wait -PassThru
        if ($enroll.ExitCode -ne 0) { throw "Sign-in receiver enrollment failed. See $dataFolder\service.log" }
    }
    if (-not $existing) { New-Service -Name $serviceName -BinaryPathName $binaryPath -DisplayName 'Glide session service' -StartupType Automatic | Out-Null }
    else {
        $changed = Invoke-CimMethod -InputObject $existing -MethodName Change -Arguments @{ PathName = $binaryPath; StartMode = 'Automatic' }
        if ($changed.ReturnValue -ne 0) { throw "Update service command failed: $($changed.ReturnValue)" }
    }
    & sc.exe description $serviceName 'Starts Glide for the enrolled console account. Opt-in paired receiver supports sign-in/unlock through a fixed SYSTEM child; no SYSTEM UI.' | Out-Null
    Check-Native 'Set service description'
    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000 | Out-Null
    Check-Native 'Set service recovery'
    if (-not $SkipFirewall) {
        foreach ($rule in @(@('Input','TCP',24819), @('Discovery','UDP',24820), @('Pairing','TCP',24821))) {
            $ruleName = 'Glide-Service-' + $rule[0]
            Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
            New-NetFirewallRule -Name $ruleName -DisplayName ('Glide service ' + $rule[0]) -Direction Inbound -Action Allow -Program $installedExe -Protocol $rule[1] -LocalPort $rule[2] -Profile Private -RemoteAddress LocalSubnet | Out-Null
        }
    }
    Assert-NoReparse $shortcutPath
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $installedExe; $shortcut.Arguments = '--open-service'; $shortcut.WorkingDirectory = $installFolder; $shortcut.Save()
    if (-not $NoStart) { Start-Service -Name $serviceName }
    Write-Output "Installed $serviceName for $ownerSid. Settings: $targetSettings"
    Write-Output 'Quit any portable Glide instance so the service can start its copy. Use the Glide (service) Start menu shortcut to reopen it.'
    if (Test-Path -LiteralPath $loginMarker) { Write-Output 'Sign-in/unlock control is enabled for a paired, unpaused Receiver role. Pair/start the receiving service PC before rebooting.' }
} catch {
    Write-Error $_
    exit 1
}
