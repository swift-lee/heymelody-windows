[CmdletBinding()]
param(
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [string]$GitRef = $env:GITHUB_REF
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ArtifactPolicy.psm1') -Force
$policy = Get-CiArtifactPolicy -EventName $EventName -GitRef $GitRef
$outputs = [ordered]@{
    'package-publish-eligible' = $policy.PackagePublishEligible.ToString().ToLowerInvariant()
    'package-retention-days' = $policy.PackageRetentionDays
    'receipt-retention-days' = $policy.ReceiptRetentionDays
}
if ($env:GITHUB_OUTPUT) {
    foreach ($entry in $outputs.GetEnumerator()) {
        "$($entry.Key)=$($entry.Value)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8NoBOM
    }
}
$policy | ConvertTo-Json -Compress
