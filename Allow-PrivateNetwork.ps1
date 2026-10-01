# Optional: run beside Glide.exe on BOTH PCs in an Administrator PowerShell window.
# Only this executable, private networks, and local-subnet peers.
# No rules are installed automatically by Glide.
$ErrorActionPreference = 'Stop'
$glideExecutable = Join-Path $PSScriptRoot 'Glide.exe'
if (-not (Test-Path -LiteralPath $glideExecutable -PathType Leaf)) {
    throw 'Place this script beside Glide.exe before running it.'
}
$glideExecutable = (Resolve-Path -LiteralPath $glideExecutable).Path
$glideRules = @(
    @{ Name = 'Glide-Portable-Private-24819'; Description = 'Glide input'; Protocol = 'TCP'; Port = 24819 },
    @{ Name = 'Glide-Portable-Discovery-24820'; Description = 'Glide discovery'; Protocol = 'UDP'; Port = 24820 },
    @{ Name = 'Glide-Portable-Pairing-24821'; Description = 'Glide pairing'; Protocol = 'TCP'; Port = 24821 }
)
foreach ($glideRule in $glideRules) {
    $existingRule = Get-NetFirewallRule -Name $glideRule.Name -ErrorAction SilentlyContinue
    if ($existingRule) {
        $existingRule | Set-NetFirewallRule -Enabled True -Direction Inbound -Action Allow -Profile Private
        $existingRule | Get-NetFirewallApplicationFilter | Set-NetFirewallApplicationFilter -Program $glideExecutable
        $existingRule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol $glideRule.Protocol -LocalPort $glideRule.Port
        $existingRule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress LocalSubnet
    } else {
        New-NetFirewallRule -Name $glideRule.Name -DisplayName $glideRule.Description `
            -Direction Inbound -Action Allow -Profile Private -Protocol $glideRule.Protocol -LocalPort $glideRule.Port `
            -Program $glideExecutable -RemoteAddress LocalSubnet | Out-Null
    }
}
Write-Host 'Glide discovery, pairing, and input are allowed on private local networks.'
Write-Host "To remove these rules: Get-NetFirewallRule -Name 'Glide-Portable-*' | Remove-NetFirewallRule"
