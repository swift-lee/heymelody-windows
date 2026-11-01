Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-CiArtifactPolicy {
    [CmdletBinding()]
    param([string]$EventName, [string]$GitRef)
    $trusted = $EventName -in @('push', 'workflow_dispatch')
    [pscustomobject]@{
        CachePublishEligible = $trusted
        CacheRetentionDays = 30
        PackagePublishEligible = $trusted
        PackageRetentionDays = $(if ($GitRef -ceq 'refs/heads/main') { 7 } else { 1 })
        ReceiptRetentionDays = 1
    }
}

Export-ModuleMember -Function Get-CiArtifactPolicy
