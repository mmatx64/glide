# Optional: run on the receiving PC in an Administrator PowerShell window.
# Only this executable, TCP 24819, Private profile, and local-subnet peers.
# No rule is installed automatically by Glide.
$ErrorActionPreference = 'Stop'
$glideExecutable = Join-Path $PSScriptRoot 'Glide.exe'
if (-not (Test-Path -LiteralPath $glideExecutable -PathType Leaf)) {
    throw 'Place this script beside Glide.exe before running it.'
}
$glideExecutable = (Resolve-Path -LiteralPath $glideExecutable).Path
$ruleName = 'Glide-Portable-Private-24819'
$existingRule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
if ($existingRule) {
    $existingRule | Set-NetFirewallRule -Enabled True -Direction Inbound -Action Allow -Profile Private
    $existingRule | Get-NetFirewallApplicationFilter | Set-NetFirewallApplicationFilter -Program $glideExecutable
    $existingRule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol TCP -LocalPort 24819
    $existingRule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress LocalSubnet
} else {
    New-NetFirewallRule -Name $ruleName -DisplayName 'Glide portable (private LAN)' `
        -Direction Inbound -Action Allow -Profile Private -Protocol TCP -LocalPort 24819 `
        -Program $glideExecutable -RemoteAddress LocalSubnet | Out-Null
}
Write-Host 'Glide is allowed on private local networks. Start listening in Glide.'
Write-Host 'To remove this rule: Remove-NetFirewallRule -Name Glide-Portable-Private-24819'
